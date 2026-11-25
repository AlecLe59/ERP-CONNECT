//! La seule commande exposée à l'interface.

use std::sync::Arc;

use erp_connect_relay::{connect_engine, Relay, CALL_TIMEOUT};
use serde_json::Value;
use tauri::{AppHandle, Emitter, State};
use tokio::sync::Mutex;

#[derive(Default)]
pub struct EngineState {
    relay: Mutex<Option<Arc<Relay>>>,
}

/// Relaie un appel JSON-RPC. Renvoie la réponse du moteur telle quelle ; une erreur de transport
/// est renvoyée sous forme de texte et l'interface affiche « moteur indisponible ».
#[tauri::command]
pub async fn engine_call(
    app: AppHandle,
    state: State<'_, EngineState>,
    method: String,
    params: Option<Value>,
) -> Result<Value, String> {
    let relay = {
        let mut guard = state.relay.lock().await;
        match guard.as_ref() {
            Some(r) if r.is_alive() => r.clone(),
            _ => {
                let handle = app.clone();
                let r = Arc::new(
                    connect_engine(move |notification| {
                        let _ = handle.emit("engine-notification", notification);
                    })
                    .await
                    .map_err(|e| e.to_string())?,
                );
                *guard = Some(r.clone());
                r
            }
        }
    };
    let result = relay
        .call(&method, params.unwrap_or(Value::Null), CALL_TIMEOUT)
        .await;
    if result.is_err() && !relay.is_alive() {
        *state.relay.lock().await = None;
    }
    result.map_err(|e| e.to_string())
}
