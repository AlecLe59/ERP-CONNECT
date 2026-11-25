/** Réponse JSON-RPC 2.0 brute renvoyée par le relais Rust. */
export interface JsonRpcResponse {
  jsonrpc: "2.0";
  id: number | string | null;
  result?: unknown;
  error?: { code: number; message: string; data?: { code?: string; correlationId?: string; fields?: { field: string; message: string }[] | null } };
}

export interface JsonRpcNotification {
  jsonrpc: "2.0";
  method: string;
  params: unknown;
}

/**
 * Transport vers le moteur. En production : la seule commande Tauri `engine_call`, relayée par Rust
 * sur le named pipe. Dans les tests : une implémentation en mémoire.
 */
export interface EngineTransport {
  call(method: string, params: unknown): Promise<JsonRpcResponse>;
  onNotification(listener: (n: JsonRpcNotification) => void): () => void;
}

/** Erreur de transport : pipe absent, service arrêté, relais en échec. */
export class TransportError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "TransportError";
  }
}

export async function createTauriTransport(): Promise<EngineTransport> {
  const { invoke } = await import("@tauri-apps/api/core");
  const { listen } = await import("@tauri-apps/api/event");
  return {
    async call(method, params) {
      try {
        return await invoke<JsonRpcResponse>("engine_call", { method, params: params ?? null });
      } catch (e) {
        throw new TransportError(typeof e === "string" ? e : "Relais indisponible");
      }
    },
    onNotification(listener) {
      let unlisten: (() => void) | undefined;
      let cancelled = false;
      void listen<JsonRpcNotification>("engine-notification", (event) => listener(event.payload)).then((u) => {
        if (cancelled) u();
        else unlisten = u;
      });
      return () => {
        cancelled = true;
        unlisten?.();
      };
    },
  };
}
