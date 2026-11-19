import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { PROTOCOL_VERSION, RPC_ERROR_CODES, type HelloResult, type RpcMethodMap, type RpcMethodName } from "@contracts";
import { EngineClient, RpcError } from "./client";
import { TransportError, type EngineTransport } from "./transport";

export type ConnectionStatus = "connecting" | "ready" | "unavailable" | "incompatible" | "denied";

interface EngineContextValue {
  status: ConnectionStatus;
  session: HelloResult | null;
  lastError: string | null;
  can: (permission: string) => boolean;
  call: <M extends RpcMethodName>(method: M, params?: RpcMethodMap[M]["params"]) => Promise<RpcMethodMap[M]["result"]>;
  client: EngineClient;
}

const EngineContext = createContext<EngineContextValue | null>(null);

export const CLIENT_VERSION = "0.2.0";

/** Ouvre la session, la rouvre si le service redémarre, et signale clairement l'indisponibilité du moteur. */
export function EngineProvider({ transport, retryMs = 3000, children }: { transport: EngineTransport; retryMs?: number; children: ReactNode }) {
  const client = useMemo(() => new EngineClient(transport), [transport]);
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  const [session, setSession] = useState<HelloResult | null>(null);
  const [lastError, setLastError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const helloing = useRef<Promise<HelloResult | null> | null>(null);

  const hello = useCallback(async (): Promise<HelloResult | null> => {
    if (helloing.current) return helloing.current;
    const p = (async () => {
      try {
        const result = await client.call("session.hello", { clientName: "ERP Connect Desktop", clientVersion: CLIENT_VERSION, protocolVersion: PROTOCOL_VERSION });
        setSession(result);
        setStatus("ready");
        setLastError(null);
        return result;
      } catch (e) {
        setSession(null);
        if (e instanceof RpcError && e.code === RPC_ERROR_CODES.ProtocolVersionMismatch) setStatus("incompatible");
        else if (e instanceof RpcError && e.code === RPC_ERROR_CODES.Forbidden) setStatus("denied");
        else setStatus("unavailable");
        setLastError(e instanceof Error ? e.message : String(e));
        return null;
      } finally {
        helloing.current = null;
      }
    })();
    helloing.current = p;
    return p;
  }, [client]);

  useEffect(() => {
    void hello();
  }, [hello, attempt]);

  // Reconnexion automatique tant que le moteur est indisponible.
  useEffect(() => {
    if (status !== "unavailable") return;
    const t = setTimeout(() => setAttempt((a) => a + 1), retryMs);
    return () => clearTimeout(t);
  }, [status, retryMs, attempt]);

  const call = useCallback(
    async <M extends RpcMethodName>(method: M, params?: RpcMethodMap[M]["params"]) => {
      try {
        return await client.call(method, params);
      } catch (e) {
        if (e instanceof TransportError || (e instanceof RpcError && e.code === RPC_ERROR_CODES.EngineUnavailable)) {
          setStatus("unavailable");
          setLastError(e.message);
        } else if (e instanceof RpcError && e.code === RPC_ERROR_CODES.Unauthenticated) {
          // Le service a redémarré : nouvelle session puis un seul nouvel essai.
          if (await hello()) return client.call(method, params);
        }
        throw e;
      }
    },
    [client, hello],
  );

  const permissions = session?.permissions;
  const can = useCallback((p: string) => permissions?.includes(p) ?? false, [permissions]);
  const value = useMemo(() => ({ status, session, lastError, can, call, client }), [status, session, lastError, can, call, client]);
  return <EngineContext.Provider value={value}>{children}</EngineContext.Provider>;
}

export function useEngine() {
  const ctx = useContext(EngineContext);
  if (!ctx) throw new Error("useEngine doit être utilisé dans EngineProvider");
  return ctx;
}
