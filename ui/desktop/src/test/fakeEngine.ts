import type { HelloResult, SettingsDto, SystemStatusResult } from "@contracts";
import { RPC_ERROR_CODES } from "@contracts";
import { TransportError, type EngineTransport, type JsonRpcNotification, type JsonRpcResponse } from "../engine/transport";

type Handler = (params: unknown) => unknown;

/** Moteur simulé POUR LES TESTS : réponses explicites, permissions vérifiées comme le vrai service. */
export class FakeEngine implements EngineTransport {
  down = false;
  calls: { method: string; params: unknown }[] = [];
  permissions: string[];
  handlers: Record<string, Handler> = {};
  private listeners = new Set<(n: JsonRpcNotification) => void>();

  constructor(permissions: string[], roles: string[] = ["administrator"]) {
    this.permissions = permissions;
    const hello: HelloResult = { sessionId: "s1", protocolVersion: "1.0", serverVersion: "0.2.0", userSid: "S-1-5-21-1", userName: "PC\\alice", roles, permissions };
    this.handlers["session.hello"] = () => ({ ...hello, permissions: this.permissions });
    this.handlers["system.getStatus"] = () => STATUS;
    this.handlers["system.getInfo"] = () => ({ installationId: "inst-1", machineName: "SRV01", serviceAccount: "NT SERVICE\\ERPConnectEngine", dataDirectory: "C:\\ProgramData\\ERPConnect\\data", logDirectory: "x", osDescription: "Windows", installedAt: "2026-11-19T10:00:00Z" });
    this.handlers["events.subscribe"] = () => ({ subscribed: ["engine.statusChanged"] });
  }

  async call(method: string, params: unknown): Promise<JsonRpcResponse> {
    this.calls.push({ method, params });
    if (this.down) throw new TransportError("Le pipe ERPConnect.Engine est introuvable");
    const handler = this.handlers[method];
    if (!handler) return { jsonrpc: "2.0", id: 1, error: { code: RPC_ERROR_CODES.MethodNotFound, message: "Méthode inconnue." } };
    try {
      return { jsonrpc: "2.0", id: 1, result: handler(params) };
    } catch (e) {
      if (e && typeof e === "object" && "code" in e) return { jsonrpc: "2.0", id: 1, error: e as JsonRpcResponse["error"] };
      throw e;
    }
  }

  onNotification(listener: (n: JsonRpcNotification) => void) {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  emit(n: JsonRpcNotification) {
    this.listeners.forEach((l) => l(n));
  }
}

export const STATUS: SystemStatusResult = {
  state: "running",
  version: "0.2.0",
  uptimeSeconds: 3725,
  dataDiskFreeBytes: 50 * 1024 ** 3,
  dataDiskTotalBytes: 200 * 1024 ** 3,
  databases: [
    { name: "config", ok: true, schemaVersion: 1 },
    { name: "runs", ok: true, schemaVersion: 1 },
    { name: "audit", ok: false, schemaVersion: 0 },
  ],
  vault: { initialized: true, activeKeyVersion: 1, secretCount: 0, protection: "DPAPI (compte du service)" },
  serverTime: "2026-11-19T12:00:00Z",
};

export const SETTINGS: SettingsDto = {
  runHeaderRetentionDays: 396,
  succeededLogRetentionDays: 30,
  failedLogRetentionDays: 90,
  rejectRetentionDays: 90,
  deduplicationRetentionDays: 396,
  logLevel: "Information",
  language: "fr-FR",
};

export const ALL_PERMISSIONS = [
  "dashboard.read", "settings.read", "settings.edit", "users.read", "users.manage", "audit.read",
  "flows.read", "flows.edit", "flows.run", "connections.use", "connections.edit", "runs.cancel", "license.manage",
];

export const OPERATOR_PERMISSIONS = ["dashboard.read", "flows.read", "flows.run", "runs.cancel", "settings.read"];
