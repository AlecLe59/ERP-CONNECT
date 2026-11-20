use std::sync::{Arc, Mutex};
use std::time::Duration;

use erp_connect_relay::{
    encode_frame, is_valid_method, read_frame, Relay, RelayError, MAX_MESSAGE_BYTES,
};
use serde_json::{json, Value};
use tokio::io::{AsyncWriteExt, DuplexStream};

#[test]
fn method_format_matches_the_service_rule() {
    for ok in [
        "session.hello",
        "system.getStatus",
        "users.assignRole",
        "a.b",
    ] {
        assert!(is_valid_method(ok), "{ok}");
    }
    for bad in [
        "",
        "session",
        ".hello",
        "session.",
        "Session.hello",
        "session.Hello",
        "a.b.c",
        "../etc",
        "sys tem.x",
        "système.x",
        "a.b1",
        "plugin:fs|read",
    ] {
        assert!(!is_valid_method(bad), "{bad}");
    }
    assert!(!is_valid_method(&format!("{}.x", "a".repeat(33))));
}

#[test]
fn frames_are_length_prefixed_little_endian() {
    let f = encode_frame(&[7u8; 258]).unwrap();
    assert_eq!(&f[..4], &[2, 1, 0, 0]);
    assert_eq!(f.len(), 262);
    assert_eq!(encode_frame(&[]), Err(RelayError::MessageTooLarge(0)));
    assert!(matches!(
        encode_frame(&vec![0u8; MAX_MESSAGE_BYTES + 1]),
        Err(RelayError::MessageTooLarge(_))
    ));
}

#[tokio::test]
async fn oversized_incoming_frame_is_refused() {
    let (mut a, mut b) = tokio::io::duplex(64);
    a.write_all(&(5u32 * 1024 * 1024).to_le_bytes())
        .await
        .unwrap();
    assert!(matches!(
        read_frame(&mut b).await,
        Err(RelayError::MessageTooLarge(_))
    ));
}

/// Faux moteur : répond à chaque requête en renvoyant la méthode reçue, après une notification.
async fn fake_engine(mut server: DuplexStream) {
    while let Ok(Some(bytes)) = read_frame(&mut server).await {
        let req: Value = serde_json::from_slice(&bytes).unwrap();
        let note = json!({"jsonrpc": "2.0", "method": "engine.statusChanged", "params": {"state": "running"}});
        server
            .write_all(&encode_frame(&serde_json::to_vec(&note).unwrap()).unwrap())
            .await
            .unwrap();
        if req["method"] == "test.silent" {
            continue;
        }
        let resp = json!({"jsonrpc": "2.0", "id": req["id"], "result": {"echo": req["method"], "params": req.get("params")}});
        server
            .write_all(&encode_frame(&serde_json::to_vec(&resp).unwrap()).unwrap())
            .await
            .unwrap();
    }
}

fn start() -> (Relay, Arc<Mutex<Vec<Value>>>) {
    let (client, server) = tokio::io::duplex(1 << 16);
    tokio::spawn(fake_engine(server));
    let notes = Arc::new(Mutex::new(Vec::new()));
    let n = notes.clone();
    (
        Relay::start(client, move |v| n.lock().unwrap().push(v)),
        notes,
    )
}

#[tokio::test]
async fn relays_calls_and_returns_the_response_unchanged() {
    let (relay, notes) = start();
    let r = relay
        .call("system.getStatus", Value::Null, Duration::from_secs(5))
        .await
        .unwrap();
    assert_eq!(r["result"]["echo"], "system.getStatus");
    assert_eq!(r["result"]["params"], Value::Null);
    let r = relay
        .call(
            "settings.update",
            json!({"expectedVersion": 3}),
            Duration::from_secs(5),
        )
        .await
        .unwrap();
    assert_eq!(r["result"]["params"]["expectedVersion"], 3);
    assert_eq!(notes.lock().unwrap().len(), 2);
}

#[tokio::test]
async fn concurrent_calls_are_correlated_by_id() {
    let (relay, _) = start();
    let relay = Arc::new(relay);
    let mut handles = Vec::new();
    for i in 0..20 {
        let r = relay.clone();
        let method = if i % 2 == 0 {
            "system.getStatus"
        } else {
            "settings.get"
        };
        handles.push(tokio::spawn(async move {
            (
                method,
                r.call(method, Value::Null, Duration::from_secs(5))
                    .await
                    .unwrap(),
            )
        }));
    }
    for h in handles {
        let (method, resp) = h.await.unwrap();
        assert_eq!(resp["result"]["echo"], method);
    }
}

#[tokio::test]
async fn invalid_method_or_params_never_reach_the_engine() {
    let (relay, notes) = start();
    assert_eq!(
        relay
            .call("plugin:fs|read_file", Value::Null, Duration::from_secs(1))
            .await,
        Err(RelayError::InvalidMethod)
    );
    assert_eq!(
        relay
            .call("system.getStatus", json!([1, 2]), Duration::from_secs(1))
            .await,
        Err(RelayError::InvalidParams)
    );
    assert!(notes.lock().unwrap().is_empty());
}

#[tokio::test]
async fn timeout_and_disconnection_are_reported() {
    let (relay, _) = start();
    assert_eq!(
        relay
            .call("test.silent", Value::Null, Duration::from_millis(200))
            .await,
        Err(RelayError::Timeout)
    );

    let (client, server) = tokio::io::duplex(1024);
    let relay = Relay::start(client, |_| {});
    drop(server);
    tokio::time::sleep(Duration::from_millis(50)).await;
    assert!(!relay.is_alive());
    assert_eq!(
        relay
            .call("system.getStatus", Value::Null, Duration::from_secs(1))
            .await,
        Err(RelayError::Disconnected)
    );
}
