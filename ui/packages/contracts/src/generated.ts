// Fichier généré par ErpConnect.Tools.ContractGen à partir de ErpConnect.Contracts.
// NE PAS MODIFIER À LA MAIN : relancer `dotnet run --project tools/ErpConnect.Tools.ContractGen`.

export const PROTOCOL_VERSION = "1.2";

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

export interface ConfigFieldDto {
  name: string;
  label: string;
  type: string;
  required: boolean;
  secret: boolean;
  default: string | null;
  choices: string[];
  help: string | null;
}

export interface ConnectionDiscoverParams {
  id: string;
  resource: string | null;
}

export interface ConnectionDiscoverResult {
  resources: string[];
  fields: DiscoveredFieldDto[];
}

export interface ConnectionDto {
  id: string;
  name: string;
  connectorId: string;
  settings: KeyValueDto[];
  secretFields: string[];
  version: number;
  updatedAt: string;
  updatedBy: string;
}

export interface ConnectionSaveParams {
  id: string | null;
  name: string;
  connectorId: string;
  settings: KeyValueDto[];
  secrets: KeyValueDto[];
  clearSecrets: string[];
  expectedVersion: number;
}

export interface ConnectionTestResultDto {
  success: boolean;
  message: string;
  durationMs: number;
}

export interface ConnectionsListResult {
  connections: ConnectionDto[];
}

export interface ConnectorDto {
  id: string;
  name: string;
  version: string;
  description: string;
  capabilities: string[];
  configFields: ConfigFieldDto[];
}

export interface ConnectorsListResult {
  connectors: ConnectorDto[];
  formats: FormatDto[];
}

export interface DatabaseStatusDto {
  name: string;
  ok: boolean;
  schemaVersion: number;
}

export interface DeleteResult {
  deleted: boolean;
}

export interface DiscoveredFieldDto {
  name: string;
  type: string;
  children: DiscoveredFieldDto[];
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

export interface FieldMapDto {
  target: string;
  expression: string;
}

export interface FlowDefinitionDto {
  source: SourceSpecDto;
  target: TargetSpecDto;
  mapping: MappingSpecDto;
  batchSize: number;
  maxRejects: number;
  steps: StepSpecDto[] | null;
}

export interface FlowDetailResult {
  flow: FlowSummaryDto;
  version: number;
  definition: FlowDefinitionDto;
  createdAt: string;
  createdBy: string;
  publishedAt: string | null;
}

export interface FlowGetParams {
  id: string;
  version: number | null;
}

export interface FlowPreviewParams {
  definition: FlowDefinitionDto;
  limit: number;
}

export interface FlowPreviewResult {
  rows: PreviewRowDto[];
  sourceFields: string[];
}

export interface FlowPublishParams {
  id: string;
  version: number;
}

export interface FlowSaveParams {
  id: string | null;
  name: string;
  description: string;
  definition: FlowDefinitionDto;
  expectedDraftVersion: number;
}

export interface FlowSummaryDto {
  id: string;
  name: string;
  description: string;
  draftVersion: number;
  publishedVersion: number | null;
  updatedAt: string;
  updatedBy: string;
}

export interface FlowsListResult {
  flows: FlowSummaryDto[];
}

export interface FormatDto {
  id: string;
  options: ConfigFieldDto[];
}

export interface FunctionDto {
  name: string;
  signature: string;
  help: string;
}

export interface FunctionsListResult {
  functions: FunctionDto[];
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

export interface IdParams {
  id: string;
}

export interface KeyValueDto {
  key: string;
  value: string | null;
}

export interface ListAssignmentsResult {
  items: RoleAssignmentDto[];
}

export interface MappingSpecDto {
  mode: string;
  fields: FieldMapDto[];
}

export interface PreviewRowDto {
  sourceJson: string;
  outputJson: string | null;
  fate: string;
  reason: string | null;
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

export interface RunCancelResult {
  accepted: boolean;
}

export interface RunChangedPayload {
  runId: string;
  flowId: string;
  status: string;
}

export interface RunDetailResult {
  run: RunDto;
  rejects: RunRejectDto[];
  logs: RunLogDto[];
}

export interface RunDto {
  id: string;
  flowId: string;
  flowVersion: number;
  status: string;
  statusLabel: string;
  triggeredBy: string;
  parentRunId: string | null;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  readCount: number;
  writtenCount: number;
  rejectedCount: number;
  skippedCount: number;
  error: string | null;
  trigger: string | null;
}

export interface RunLogDto {
  at: string;
  level: string;
  message: string;
}

export interface RunRejectDto {
  batchNumber: number;
  recordIndex: number;
  stage: string;
  reason: string;
  recordJson: string | null;
  at: string;
}

export interface RunStartParams {
  flowId: string;
  useDraft: boolean;
}

export interface RunsListParams {
  flowId: string | null;
  status: string | null;
  page: number;
  pageSize: number;
}

export interface RunsListResult {
  items: RunDto[];
  total: number;
  page: number;
  pageSize: number;
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

export interface SourceSpecDto {
  connectionId: string;
  resource: string;
  options: KeyValueDto[];
}

export interface StepSpecDto {
  kind: string;
  field: string | null;
  expression: string | null;
  target: string | null;
  message: string | null;
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

export interface TargetSpecDto {
  connectionId: string;
  resource: string;
  mode: string;
  options: KeyValueDto[];
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
  "connectors.list": { params: EmptyParams; result: ConnectorsListResult };
  "connections.list": { params: EmptyParams; result: ConnectionsListResult };
  "connections.save": { params: ConnectionSaveParams; result: ConnectionDto };
  "connections.delete": { params: IdParams; result: DeleteResult };
  "connections.test": { params: IdParams; result: ConnectionTestResultDto };
  "flows.list": { params: EmptyParams; result: FlowsListResult };
  "flows.get": { params: FlowGetParams; result: FlowDetailResult };
  "flows.save": { params: FlowSaveParams; result: FlowSummaryDto };
  "flows.publish": { params: FlowPublishParams; result: FlowSummaryDto };
  "runs.start": { params: RunStartParams; result: RunDto };
  "runs.list": { params: RunsListParams; result: RunsListResult };
  "runs.get": { params: IdParams; result: RunDetailResult };
  "runs.cancel": { params: IdParams; result: RunCancelResult };
  "connections.discover": { params: ConnectionDiscoverParams; result: ConnectionDiscoverResult };
  "flows.preview": { params: FlowPreviewParams; result: FlowPreviewResult };
  "flows.functions": { params: EmptyParams; result: FunctionsListResult };
}

export type RpcMethodName = keyof RpcMethodMap;

export interface RpcNotificationMap {
  "engine.statusChanged": EngineStatusChangedPayload;
  "settings.changed": SettingsChangedPayload;
  "audit.appended": AuditAppendedPayload;
  "runs.changed": RunChangedPayload;
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
  "connectors.list": "flows.read",
  "connections.list": "flows.read",
  "connections.save": "connections.edit",
  "connections.delete": "connections.edit",
  "connections.test": "connections.use",
  "flows.list": "flows.read",
  "flows.get": "flows.read",
  "flows.save": "flows.edit",
  "flows.publish": "flows.edit",
  "runs.start": "flows.run",
  "runs.list": "flows.read",
  "runs.get": "flows.read",
  "runs.cancel": "runs.cancel",
  "connections.discover": "connections.use",
  "flows.preview": "flows.edit",
  "flows.functions": "flows.read",
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
