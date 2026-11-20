# Protocole JSON-RPC interface ↔ moteur, version 1.0

Source unique : `src/ErpConnect.Contracts`. Le client TypeScript (`ui/packages/contracts/src/generated.ts`) et
l'instantané `src/ErpConnect.Contracts/contract.snapshot.json` sont générés par
`dotnet run --project tools/ErpConnect.Tools.ContractGen`. La CI échoue si l'un des deux n'est pas à jour.

## Transport

| Élément | Règle | Où c'est vérifié |
| --- | --- | --- |
| Pipe | `\\.\pipe\ERPConnect.Engine`, ACL : groupes ERP Connect + Administrateurs, NETWORK refusé | `WindowsPipeFactory` |
| Découpage | longueur u32 petit-boutiste puis JSON UTF-8 | `MessageFraming` (C#), `relay` (Rust) |
| Taille | 4 Mo maximum, refus sans lire le contenu | FramingTests, S12 |
| Profondeur | 64 niveaux | DispatcherTests, S12 |
| Connexions | 32 simultanées | PipeServerTests, S13 |
| Inactivité | fermeture après 30 min | PipeServerTests |
| Corrélation | `<session>-<n>` dans `error.data.correlationId` et les journaux | DispatcherTests |
| Lots (batch) | refusés (`-32600`) | DispatcherTests |

## Session

Premier appel obligatoire : `session.hello { clientName, clientVersion, protocolVersion }`. Réponse : identité
Windows reconnue, rôles et permissions effectives. Un compte sans aucun rôle est refusé (`FORBIDDEN`, audité).
Version majeure différente : `PROTOCOL_VERSION_MISMATCH`. Les permissions sont recalculées à chaque appel.

## Méthodes (Phase 2)

| Méthode | Permission |
| --- | --- |
| session.hello | aucune (ACL du pipe) |
| system.getStatus, system.getInfo | dashboard.read |
| settings.get / settings.update | settings.read / settings.edit |
| users.listAssignments | users.read |
| users.assignRole, users.revokeRole, users.resolvePrincipal | users.manage |
| audit.query, audit.verifyChain | audit.read |
| vault.getStatus | settings.read |
| events.subscribe / events.unsubscribe | permission de chaque sujet |

Notifications : `engine.statusChanged` (dashboard.read), `settings.changed` (settings.read),
`audit.appended` (audit.read).

## Erreurs

Codes standard `-32700` à `-32603` ; erreurs métier `-32001` UNAUTHENTICATED, `-32002` FORBIDDEN, `-32003`
NOT_FOUND, `-32004` CONFLICT, `-32005` VALIDATION_FAILED (avec `data.fields`), `-32006`
PROTOCOL_VERSION_MISMATCH, `-32010` ENGINE_UNAVAILABLE, `-32011` ENTITLEMENT_DENIED. Un message d'erreur ne
contient jamais de secret, de chemin interne ni de trace.
