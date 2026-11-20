# Décisions d'architecture (ADR)

| ADR | Décision | Statut |
| --- | --- | --- |
| [0001](0001-moteur-dotnet-10.md) | Moteur d'intégration en .NET 10 LTS | Accepté |
| [0002](0002-interface-tauri-react.md) | Interface Tauri 2 + React + TypeScript | Accepté |
| [0003](0003-rust-relais-uniquement.md) | Couche Rust limitée au relais | Accepté |
| [0004](0004-jsonrpc-named-pipe.md) | JSON-RPC 2.0 sur named pipe avec ACL | Accepté |
| [0005](0005-authentification-windows.md) | Authentification locale par identité Windows | Accepté |
| [0006](0006-service-moindre-privilege.md) | Service sous compte à privilèges minimaux | Accepté |
| [0007](0007-secrets-dpapi-aes-gcm.md) | Coffre de secrets DPAPI + AES-256-GCM | Accepté |
| [0008](0008-scripts-jint-puis-python.md) | Scripts JavaScript (Jint) avant Python isolé | Accepté |
| [0009](0009-installateur-msi-wix.md) | Installateur MSI avec WiX | Accepté |
| [0010](0010-aucun-port-entrant.md) | Aucun port entrant par défaut | Accepté |
| [0011](0011-agent-sortant-facultatif.md) | Agent distant facultatif, HTTPS sortant | Remplacé par 0016 |
| [0012](0012-control-center-deploiement.md) | Control Center déployable partout | Remplacé par 0016 |
| [0013](0013-statut-annule.md) | Statut « Annulé » | Accepté |
| [0014](0014-jsonrpc-implementation-maison.md) | Routage JSON-RPC écrit dans le projet | Proposé |
| [0015](0015-verification-serveur-proprietaire-pipe.md) | Vérification du serveur par le propriétaire du pipe | Proposé |
| [0016](0016-agent-plateforme-trois-modes.md) | Agent de contrôle et plateforme, trois modes, un seul code | Accepté |
| [0017](0017-politique-de-transfert.md) | Politique de transfert signée sur l'agent, niveau 0 par défaut | Accepté |
| [0018](0018-bibliotheque-controle-partagee.md) | Bibliothèque de contrôle partagée | Accepté |
| [0019](0019-identite-certificats-agents.md) | Un certificat par agent, renouvelable et révocable | Accepté |
| [0020](0020-isolation-des-clients.md) | Une base et une clé par client | Accepté |
| [0021](0021-mises-a-jour-signees.md) | Mises à jour d'agent signées, service séparé | Accepté |
| [0022](0022-plateforme-aspnetcore-postgresql.md) | Plateforme ASP.NET Core + PostgreSQL | Accepté sous réserve |
| [0023](0023-validation-tests-securite-windows.md) | Tests de sécurité systématiques, validation Windows avant production | Accepté |

Nouvelle décision : copier [0000-modele.md](0000-modele.md).
