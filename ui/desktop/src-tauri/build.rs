fn main() {
    // Seule la commande engine_call existe ; toute autre commande est refusée par Tauri (S15).
    tauri_build::try_build(
        tauri_build::Attributes::new()
            .app_manifest(tauri_build::AppManifest::new().commands(&["engine_call"])),
    )
    .expect("échec de tauri-build");
}
