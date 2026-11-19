//! S15 : la configuration n'autorise que engine_call, aucun plugin système, et une CSP stricte.

use serde_json::Value;

fn read_json(path: &str) -> Value {
    let full = format!("{}/{}", env!("CARGO_MANIFEST_DIR"), path);
    serde_json::from_str(&std::fs::read_to_string(&full).unwrap_or_else(|_| panic!("{full}")))
        .unwrap()
}

#[test]
fn capability_allows_only_engine_call_and_event_listening() {
    let cap = read_json("capabilities/default.json");
    let perms: Vec<&str> = cap["permissions"]
        .as_array()
        .unwrap()
        .iter()
        .map(|p| p.as_str().unwrap())
        .collect();
    assert_eq!(
        perms,
        [
            "allow-engine-call",
            "core:event:allow-listen",
            "core:event:allow-unlisten"
        ]
    );
    let capability_files =
        std::fs::read_dir(format!("{}/capabilities", env!("CARGO_MANIFEST_DIR")))
            .unwrap()
            .count();
    assert_eq!(capability_files, 1, "aucune autre capacité ne doit exister");
}

#[test]
fn no_tauri_plugin_dependency() {
    let manifest =
        std::fs::read_to_string(format!("{}/Cargo.toml", env!("CARGO_MANIFEST_DIR"))).unwrap();
    assert!(
        !manifest.contains("tauri-plugin"),
        "aucun plugin Tauri n'est autorisé"
    );
}

#[test]
fn csp_forbids_external_scripts_and_network() {
    let conf = read_json("tauri.conf.json");
    let csp = conf["app"]["security"]["csp"].as_str().unwrap();
    assert!(csp.contains("default-src 'self'"));
    assert!(csp.contains("script-src 'self'"));
    assert!(csp.contains("connect-src ipc: http://ipc.localhost"));
    assert!(
        !csp.contains("unsafe-inline")
            && !csp.contains("unsafe-eval")
            && !csp.contains("http://*")
            && !csp.contains("https:")
    );
    assert_eq!(conf["app"]["withGlobalTauri"], Value::Bool(false));
}

#[test]
fn only_one_command_is_registered() {
    let dir = format!("{}/src", env!("CARGO_MANIFEST_DIR"));
    let lib = std::fs::read_to_string(format!("{dir}/lib.rs")).unwrap();
    assert!(lib.contains("generate_handler![commands::engine_call]"));
    let commands: usize = std::fs::read_dir(&dir)
        .unwrap()
        .map(|e| {
            std::fs::read_to_string(e.unwrap().path())
                .unwrap()
                .matches("#[tauri::command]")
                .count()
        })
        .sum();
    assert_eq!(commands, 1);
}
