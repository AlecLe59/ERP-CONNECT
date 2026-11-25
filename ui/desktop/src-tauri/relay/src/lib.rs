//! Relais JSON-RPC entre l'interface et le moteur (ADR 0003, ADR 0004).
//!
//! Aucune logique métier : le relais vérifie le format du nom de méthode, encadre le message
//! (longueur sur 4 octets petit-boutiste puis JSON UTF-8), l'envoie sur le named pipe et renvoie la
//! réponse telle quelle. Les notifications du moteur sont transmises à l'appelant.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use std::time::Duration;

use serde_json::{json, Value};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, WriteHalf};
use tokio::sync::{oneshot, Mutex};

#[cfg(windows)]
pub mod windows;

/// Nom complet du pipe du moteur.
pub const PIPE_PATH: &str = r"\\.\pipe\ERPConnect.Engine";
/// Taille maximale d'un message, identique au service.
pub const MAX_MESSAGE_BYTES: usize = 4 * 1024 * 1024;
/// Délai maximal d'attente d'une réponse.
pub const CALL_TIMEOUT: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RelayError {
    InvalidMethod,
    InvalidParams,
    MessageTooLarge(usize),
    Disconnected,
    Timeout,
    InvalidResponse,
    UntrustedServer(String),
    Unavailable(String),
}

impl std::fmt::Display for RelayError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RelayError::InvalidMethod => write!(
                f,
                "Nom de méthode refusé par le relais (format domaine.action attendu)"
            ),
            RelayError::InvalidParams => write!(f, "Les paramètres doivent être un objet JSON"),
            RelayError::MessageTooLarge(n) => write!(
                f,
                "Message de {n} octets refusé (maximum {MAX_MESSAGE_BYTES})"
            ),
            RelayError::Disconnected => write!(f, "Connexion au moteur perdue"),
            RelayError::Timeout => write!(f, "Le moteur n'a pas répondu à temps"),
            RelayError::InvalidResponse => write!(f, "Réponse du moteur illisible"),
            RelayError::UntrustedServer(why) => write!(f, "Serveur du pipe non reconnu : {why}"),
            RelayError::Unavailable(why) => write!(f, "Moteur indisponible : {why}"),
        }
    }
}

impl std::error::Error for RelayError {}

/// Vérifie le format `domaine.action` (même règle que le service).
pub fn is_valid_method(method: &str) -> bool {
    let Some((domain, action)) = method.split_once('.') else {
        return false;
    };
    let ok = |s: &str, max: usize| {
        let mut chars = s.chars();
        matches!(chars.next(), Some(c) if c.is_ascii_lowercase())
            && s.len() <= max
            && chars.all(|c| c.is_ascii_alphabetic())
    };
    ok(domain, 32) && ok(action, 64)
}

/// Encadre un message : longueur u32 petit-boutiste puis contenu.
pub fn encode_frame(payload: &[u8]) -> Result<Vec<u8>, RelayError> {
    if payload.is_empty() || payload.len() > MAX_MESSAGE_BYTES {
        return Err(RelayError::MessageTooLarge(payload.len()));
    }
    let mut out = Vec::with_capacity(4 + payload.len());
    out.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    out.extend_from_slice(payload);
    Ok(out)
}

/// Lit un message. `Ok(None)` si le flux est fermé proprement avant un en-tête.
pub async fn read_frame<R: AsyncRead + Unpin>(
    reader: &mut R,
) -> Result<Option<Vec<u8>>, RelayError> {
    let mut header = [0u8; 4];
    match reader.read_exact(&mut header).await {
        Ok(_) => {}
        Err(e) if e.kind() == std::io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(_) => return Err(RelayError::Disconnected),
    }
    let len = u32::from_le_bytes(header) as usize;
    if len == 0 || len > MAX_MESSAGE_BYTES {
        return Err(RelayError::MessageTooLarge(len));
    }
    let mut payload = vec![0u8; len];
    reader
        .read_exact(&mut payload)
        .await
        .map_err(|_| RelayError::Disconnected)?;
    Ok(Some(payload))
}

type Pending = Arc<StdMutex<HashMap<u64, oneshot::Sender<Value>>>>;

/// Connexion relayée au moteur. Une tâche lit en continu : réponses vers l'appel en attente,
/// notifications vers le rappel fourni.
pub struct Relay {
    writer: Mutex<WriteHalf<Box<dyn Duplex>>>,
    pending: Pending,
    next_id: AtomicU64,
    alive: Arc<AtomicBool>,
}

/// Flux bidirectionnel (pipe nommé en production, flux mémoire dans les tests).
pub trait Duplex: AsyncRead + AsyncWrite + Send + Unpin {}
impl<T: AsyncRead + AsyncWrite + Send + Unpin> Duplex for T {}

impl Relay {
    /// Démarre le relais sur un flux déjà connecté et vérifié.
    pub fn start<S, F>(stream: S, on_notification: F) -> Relay
    where
        S: Duplex + 'static,
        F: Fn(Value) + Send + Sync + 'static,
    {
        let boxed: Box<dyn Duplex> = Box::new(stream);
        let (mut reader, writer) = tokio::io::split(boxed);
        let pending: Pending = Arc::new(StdMutex::new(HashMap::new()));
        let alive = Arc::new(AtomicBool::new(true));
        let (p, a) = (pending.clone(), alive.clone());
        tokio::spawn(async move {
            while let Ok(Some(bytes)) = read_frame(&mut reader).await {
                let Ok(value) = serde_json::from_slice::<Value>(&bytes) else {
                    break;
                };
                match value.get("id").and_then(Value::as_u64) {
                    Some(id) => {
                        let sender = p.lock().ok().and_then(|mut m| m.remove(&id));
                        if let Some(tx) = sender {
                            let _ = tx.send(value);
                        }
                    }
                    None if value.get("method").is_some() => on_notification(value),
                    None => {}
                }
            }
            a.store(false, Ordering::SeqCst);
            if let Ok(mut m) = p.lock() {
                m.clear(); // les appels en attente reçoivent Disconnected
            }
        });
        Relay {
            writer: Mutex::new(writer),
            pending,
            next_id: AtomicU64::new(1),
            alive,
        }
    }

    pub fn is_alive(&self) -> bool {
        self.alive.load(Ordering::SeqCst)
    }

    /// Relaie un appel et renvoie la réponse JSON-RPC complète, sans l'interpréter.
    pub async fn call(
        &self,
        method: &str,
        params: Value,
        timeout: Duration,
    ) -> Result<Value, RelayError> {
        if !is_valid_method(method) {
            return Err(RelayError::InvalidMethod);
        }
        if !(params.is_object() || params.is_null()) {
            return Err(RelayError::InvalidParams);
        }
        if !self.is_alive() {
            return Err(RelayError::Disconnected);
        }
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let mut request = json!({ "jsonrpc": "2.0", "id": id, "method": method });
        if !params.is_null() {
            request["params"] = params;
        }
        let bytes = serde_json::to_vec(&request).map_err(|_| RelayError::InvalidParams)?;
        let frame = encode_frame(&bytes)?;

        let (tx, rx) = oneshot::channel();
        self.pending
            .lock()
            .map_err(|_| RelayError::Disconnected)?
            .insert(id, tx);
        {
            let mut w = self.writer.lock().await;
            if w.write_all(&frame).await.is_err() || w.flush().await.is_err() {
                self.forget(id);
                return Err(RelayError::Disconnected);
            }
        }
        match tokio::time::timeout(timeout, rx).await {
            Ok(Ok(v)) if v.get("jsonrpc") == Some(&Value::from("2.0")) => Ok(v),
            Ok(Ok(_)) => Err(RelayError::InvalidResponse),
            Ok(Err(_)) => Err(RelayError::Disconnected),
            Err(_) => {
                self.forget(id);
                Err(RelayError::Timeout)
            }
        }
    }

    fn forget(&self, id: u64) {
        if let Ok(mut m) = self.pending.lock() {
            m.remove(&id);
        }
    }
}

/// Ouvre et vérifie la connexion au moteur. Windows uniquement.
pub async fn connect_engine<F>(on_notification: F) -> Result<Relay, RelayError>
where
    F: Fn(Value) + Send + Sync + 'static,
{
    #[cfg(windows)]
    {
        let client = windows::connect_verified().await?;
        Ok(Relay::start(client, on_notification))
    }
    #[cfg(not(windows))]
    {
        let _ = on_notification;
        Err(RelayError::Unavailable(
            "le moteur ERP Connect n'existe que sous Windows".into(),
        ))
    }
}
