import type { ConnectionDto, ConnectorsListResult, RunDto } from "@contracts";

export const CONNECTORS: ConnectorsListResult = {
  connectors: [
    { id: "file.local", name: "Dossier local", version: "1.0.0", description: "", capabilities: ["read", "write"], configFields: [
      { name: "rootPath", label: "Dossier racine", type: "path", required: true, secret: false, default: null, choices: [], help: null },
    ] },
    { id: "rest", name: "API REST (JSON)", version: "1.0.0", description: "", capabilities: ["read", "write"], configFields: [
      { name: "baseUrl", label: "URL de base", type: "url", required: true, secret: false, default: null, choices: [], help: null },
      { name: "auth", label: "Authentification", type: "choice", required: false, secret: false, default: "none", choices: ["none", "bearer"], help: null },
      { name: "token", label: "Jeton (bearer)", type: "text", required: false, secret: true, default: null, choices: [], help: null },
    ] },
  ],
  formats: [
    { id: "csv", options: [{ name: "delimiter", label: "Séparateur", type: "choice", required: false, secret: false, default: ";", choices: [";", ","], help: null }] },
    { id: "json", options: [] },
  ],
};

export const LOCAL: ConnectionDto = {
  id: "entrees", name: "Entrées", connectorId: "file.local", settings: [{ key: "rootPath", value: "D:\\Echanges" }], secretFields: [], version: 1,
  updatedAt: "2026-11-19T10:00:00Z", updatedBy: "PC\\alice",
};

export const API: ConnectionDto = {
  id: "erp", name: "ERP", connectorId: "rest", settings: [{ key: "baseUrl", value: "https://erp.example/" }, { key: "auth", value: "bearer" }], secretFields: ["token"], version: 3,
  updatedAt: "2026-11-19T10:00:00Z", updatedBy: "PC\\alice",
};

export const RUN: RunDto = {
  id: "20261119-120000-abcd1234", flowId: "clients", flowVersion: 2, status: "failed", statusLabel: "Échec", triggeredBy: "PC\\alice", parentRunId: null,
  createdAt: "2026-11-19T12:00:00Z", startedAt: "2026-11-19T12:00:01Z", endedAt: "2026-11-19T12:00:05Z", readCount: 10, writtenCount: 9, rejectedCount: 1, skippedCount: 0,
  error: "1 rejet(s) : l'exécution est en échec.", trigger: "manual", errorKind: "rejects",
};
