//! Coquille Tauri : une seule commande, `engine_call`, qui relaie vers le moteur (ADR 0003).

mod commands;

pub use commands::EngineState;

pub fn run() {
    tauri::Builder::default()
        .manage(EngineState::default())
        .invoke_handler(tauri::generate_handler![commands::engine_call])
        .run(tauri::generate_context!())
        .expect("échec du démarrage de l'application");
}
