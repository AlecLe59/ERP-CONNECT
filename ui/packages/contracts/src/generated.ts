// Fichier généré par ErpConnect.Tools.ContractGen à partir de ErpConnect.Contracts.
// NE PAS MODIFIER À LA MAIN : relancer `dotnet run --project tools/ErpConnect.Tools.ContractGen`.

export const PROTOCOL_VERSION = "1.0";

export interface AssignRoleParams {
  principalSid: string;
  principalName: string;
  principalKind: string;
  role: string;
}

export interface AuditAppendedPayload {
  sequence: number;
  action: string;
  outcome: string;
}

export interface AuditEventDto {
  sequence: number;
  timestamp: string;
  actorSid: string;
  actorName: string;
  action: string;
  target: string;
  outcome: string;
  details: string | null;
  correlationId: string;
  hash: string;
}

export interface AuditQueryParams {
  page: number;
  pageSize: number;
  action: string | null;
  from: string | null;
  to: string | null;
}

export interface AuditQueryResult {
  items: AuditEventDto[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AuditVerifyResult {
  valid: boolean;
  checkedCount: number;
  firstInvalidSequence: number | null;
  reason: string | null;
}

export interface DatabaseStatusDto {
  name: string;
  ok: boolean;
  schemaVersion: number;
}

export interface EmptyParams {
}

export interface EngineStatusChangedPayload {
  state: string;
  at: string;
}

export interface EventsSubscribeParams {
  topics: string[];
}

export interface EventsSubscribeResult {
  subscribed: string[];
}

export interface FieldErrorDto {
  field: string;
  message: string;
}

export interface HelloParams {
  clientName: string;
  clientVersion: string;
  protocolVersion: string;
}

export interface HelloResult {
  sessionId: string;
  protocolVersion: string;
  serverVersion: string;
  userSid: string;
  userName: string;
  roles: string[];
  permissions: string[];
}

export interface ListAssignmentsResult {
  items: RoleAssignmentDto[];
}

export interface ResolvePrincipalParams {
  name: string;
}

export interface ResolvePrincipalResult {
  found: boolean;
  sid: string | null;
  name: string | null;
  kind: string | null;
}

export interface RevokeRoleParams {
  assignmentId: number;
}

export interface RevokeRoleResult {
  revoked: boolean;
}

export interface RoleAssignmentDto {
  id: number;
  principalSid: string;
  principalName: string;
  principalKind: string;
  role: string;
  scope: string;
  createdAt: string;
  createdBy: string;
}

export interface RpcErrorData {
  code: string;
  correlationId: string;
  fields: FieldErrorDto[] | null;
}

export interface SettingsChangedPayload {
  version: number;
  updatedBy: string;
}

export interface SettingsDto {
  runHeaderRetentionDays: number;
  succeededLogRetentionDays: number;
  failedLogRetentionDays: number;
  rejectRetentionDays: number;
  deduplicationRetentionDays: number;
  logLevel: string;
  language: string;
}

export interface SettingsResult {
  settings: SettingsDto;
  version: number;
  updatedAt: string;
  updatedBy: string;
}

export interface SettingsUpdateParams {
  settings: SettingsDto;
  expectedVersion: number;
}

export interface SystemInfoResult {
  installationId: string;
  machineName: string;
  serviceAccount: string;
  dataDirectory: string;
  logDirectory: string;
  osDescription: string;
  installedAt: string;
}

export interface SystemStatusResult {
  state: string;
  version: string;
  uptimeSeconds: number;
  dataDiskFreeBytes: number;
  dataDiskTotalBytes: number;
  databases: DatabaseStatusDto[];
  vault: VaultStatusResult;
  serverTime: string;
}

export interface VaultStatusResult {
  initialized: boolean;
  activeKeyVersion: number | null;
  secretCount: number;
  protection: string;
}

export interface RpcMethodMap {
  "session.hello": { params: HelloParams; result: HelloResult };
  "system.getStatus": { params: EmptyParams; result: SystemStatusResult };
  "system.getInfo": { params: EmptyParams; result: SystemInfoResult };
  "settings.get": { params: EmptyParams; result: SettingsResult };
  "settings.update": { params: SettingsUpdateParams; result: SettingsResult };
  "users.listAssignments": { params: EmptyParams; result: ListAssignmentsResult };
  "users.assignRole": { params: AssignRoleParams; result: RoleAssignmentDto };
  "users.revokeRole": { params: RevokeRoleParams; result: RevokeRoleResult };
  "users.resolvePrincipal": { params: ResolvePrincipalParams; result: ResolvePrincipalResult };
  "audit.query": { params: AuditQueryParams; result: AuditQueryResult };
  "audit.verifyChain": { params: EmptyParams; result: AuditVerifyResult };
  "vault.getStatus": { params: EmptyParams; result: VaultStatusResult };
  "events.subscribe": { params: EventsSubscribeParams; result: EventsSubscribeResult };
  "events.unsubscribe": { params: EventsSubscribeParams; result: EventsSubscribeResult };
}

export type RpcMethodName = keyof RpcMethodMap;

export interface RpcNotificationMap {
  "engine.statusChanged": EngineStatusChangedPayload;
  "settings.changed": SettingsChangedPayload;
  "audit.appended": AuditAppendedPayload;
}

export type RpcNotificationName = keyof RpcNotificationMap;

export const METHOD_PERMISSIONS: Record<RpcMethodName, string | null> = {
  "session.hello": null,
  "system.getStatus": "dashboard.read",
  "system.getInfo": "dashboard.read",
  "settings.get": "settings.read",
  "settings.update": "settings.edit",
  "users.listAssignments": "users.read",
  "users.assignRole": "users.manage",
  "users.revokeRole": "users.manage",
  "users.resolvePrincipal": "users.manage",
  "audit.query": "audit.read",
  "audit.verifyChain": "audit.read",
  "vault.getStatus": "settings.read",
  "events.subscribe": null,
  "events.unsubscribe": null,
};

export const RPC_ERROR_CODES = {
  ParseError: -32700,
  InvalidRequest: -32600,
  MethodNotFound: -32601,
  InvalidParams: -32602,
  InternalError: -32603,
  Unauthenticated: -32001,
  Forbidden: -32002,
  NotFound: -32003,
  Conflict: -32004,
  ValidationFailed: -32005,
  ProtocolVersionMismatch: -32006,
  EngineUnavailable: -32010,
  EntitlementDenied: -32011,
} as const;
