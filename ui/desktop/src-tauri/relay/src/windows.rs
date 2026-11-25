//! Connexion au pipe et vérification du serveur (Windows uniquement).
//!
//! Avant tout envoi, le relais vérifie que le propriétaire de l'objet pipe est le compte du service
//! `NT SERVICE\ERPConnectEngine`. Un programme qui aurait créé un faux pipe du même nom ne peut pas
//! s'attribuer ce propriétaire (il faudrait le privilège de restauration) : il ne reçoit donc aucune requête.
//! La connexion est ouverte au niveau d'emprunt d'identité « Identification » : le service peut lire le jeton
//! de l'utilisateur, mais pas agir en son nom.

use std::os::windows::io::AsRawHandle;
use std::time::Duration;

use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
use windows_sys::Win32::Foundation::{LocalFree, ERROR_PIPE_BUSY, ERROR_SUCCESS};
use windows_sys::Win32::Security::Authorization::{GetSecurityInfo, SE_KERNEL_OBJECT};
use windows_sys::Win32::Security::{
    EqualSid, IsValidSid, LookupAccountNameW, OWNER_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR,
    PSID, SID_NAME_USE,
};
use windows_sys::Win32::Storage::FileSystem::SECURITY_IDENTIFICATION;

use crate::{RelayError, PIPE_PATH};

pub const SERVICE_ACCOUNT: &str = r"NT SERVICE\ERPConnectEngine";

pub async fn connect_verified() -> Result<NamedPipeClient, RelayError> {
    let mut attempts = 0;
    let client = loop {
        match ClientOptions::new()
            .security_qos_flags(SECURITY_IDENTIFICATION)
            .open(PIPE_PATH)
        {
            Ok(c) => break c,
            Err(e) if e.raw_os_error() == Some(ERROR_PIPE_BUSY as i32) && attempts < 40 => {
                attempts += 1;
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            Err(e) => return Err(RelayError::Unavailable(e.to_string())),
        }
    };
    verify_owner(&client)?;
    Ok(client)
}

fn verify_owner(client: &NamedPipeClient) -> Result<(), RelayError> {
    if allow_untrusted_dev_server() {
        return Ok(());
    }
    let expected = account_sid(SERVICE_ACCOUNT).map_err(RelayError::UntrustedServer)?;
    let mut owner: PSID = std::ptr::null_mut();
    let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY : handle valide pendant l'appel ; owner pointe dans sd, libéré par LocalFree.
    let status = unsafe {
        GetSecurityInfo(
            client.as_raw_handle() as _,
            SE_KERNEL_OBJECT,
            OWNER_SECURITY_INFORMATION,
            &mut owner,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            &mut sd,
        )
    };
    if status != ERROR_SUCCESS {
        return Err(RelayError::UntrustedServer(format!(
            "propriétaire illisible (erreur {status})"
        )));
    }
    // SAFETY : owner et expected sont des SID valides.
    let same = unsafe { IsValidSid(owner) != 0 && EqualSid(owner, expected.as_ptr() as PSID) != 0 };
    // SAFETY : sd a été alloué par GetSecurityInfo.
    unsafe { LocalFree(sd as _) };
    if same {
        Ok(())
    } else {
        Err(RelayError::UntrustedServer(format!(
            "le pipe n'appartient pas à {SERVICE_ACCOUNT}"
        )))
    }
}

fn account_sid(account: &str) -> Result<Vec<u8>, String> {
    let name: Vec<u16> = account.encode_utf16().chain(std::iter::once(0)).collect();
    let mut sid = vec![0u8; 68];
    let mut sid_len = sid.len() as u32;
    let mut domain = vec![0u16; 256];
    let mut domain_len = domain.len() as u32;
    let mut use_: SID_NAME_USE = 0;
    // SAFETY : tampons dimensionnés et longueurs passées.
    let ok = unsafe {
        LookupAccountNameW(
            std::ptr::null(),
            name.as_ptr(),
            sid.as_mut_ptr() as PSID,
            &mut sid_len,
            domain.as_mut_ptr(),
            &mut domain_len,
            &mut use_,
        )
    };
    if ok == 0 {
        return Err(format!(
            "compte {account} introuvable : le service est-il installé ?"
        ));
    }
    sid.truncate(sid_len as usize);
    Ok(sid)
}

/// Uniquement dans une compilation de développement (debug), pour lancer le moteur en console sous un
/// compte développeur : `ERPCONNECT_DEV_ALLOW_ANY_SERVER=1`. Sans effet dans une compilation de production.
fn allow_untrusted_dev_server() -> bool {
    cfg!(debug_assertions) && std::env::var("ERPCONNECT_DEV_ALLOW_ANY_SERVER").as_deref() == Ok("1")
}
