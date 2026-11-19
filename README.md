# ERP CONNECT

Logiciel d'intégration universelle de données pour Windows, installé chez le client.

**Principe :** SOURCE → TRANSFORMATION → DESTINATION

> **État du projet : Phase 2 (socle Windows) développée, en attente de validation sur Windows.**
> Le socle compile et ses tests passent sous Linux (voir [docs/phases/phase-2-rapport.md](docs/phases/phase-2-rapport.md)).
> Les parties propres à Windows (service installé, ACL, DPAPI, MSI) sont écrites mais **pas encore testées sur
> Windows** : rien n'est déclaré opérationnel chez un client avant cette validation.
> Aucun connecteur ni flux n'existe encore.

## Architecture

| Composant | Technologie | Dossier |
| --- | --- | --- |
| Application desktop | Tauri 2 + React + TypeScript | `ui/desktop` |
| Moteur d'intégration | .NET 10 LTS | `src/ErpConnect.Engine` |
| Service Windows | .NET 10 Worker Service | `src/ErpConnect.Service` |
| Communication locale | JSON-RPC 2.0 sur named pipe (ACL) | `src/ErpConnect.Rpc` |
| Stockage local | SQLite (WAL) | `src/ErpConnect.Storage` |
| Services métier | .NET 10 | `src/ErpConnect.Engine` |
| Contrat JSON-RPC | C# → TypeScript généré | `src/ErpConnect.Contracts`, `ui/packages/contracts` |
| Secrets | Clé maître DPAPI + AES-256-GCM | `src/ErpConnect.Security` |
| Installateur | MSI (WiX Toolset) | `installer/` |
| Agent distant (facultatif) | .NET 10, HTTPS sortant uniquement | Phase 7 |
| Control Center | ASP.NET Core + PostgreSQL + React | Phase 7 |

Décisions d'architecture : [`docs/adr/`](docs/adr/README.md).

## Arborescence

```
ERP-CONNECT/
├── .github/            Workflows CI, modèles d'issues et de PR, règles de branches
├── docs/
│   ├── adr/            Décisions d'architecture (ADR)
│   ├── architecture/   Documentation technique
│   ├── phases/         Plans et rapports de chaque phase
│   └── security/       Documentation de sécurité destinée aux DSI
├── src/                Code .NET (moteur, service, RPC, stockage, sécurité)
├── tests/              Tests .NET (unitaires, intégration, sécurité)
├── ui/                 Application desktop Tauri + React
├── installer/          Projet WiX (MSI)
└── tools/              Outils de développement (génération du contrat TypeScript, scripts)
```

## Prérequis de développement

- Windows 10/11 ou Windows Server 2019+ (le service et les tests d'intégration exigent Windows)
- SDK .NET 10 (version fixée par `global.json`)
- Node.js 24 LTS
- Rust stable (pour la coquille Tauri)
- WebView2 Runtime

## Commandes

```powershell
# .NET : compilation et tests (les tests Windows sont ignorés hors Windows)
dotnet build ErpConnect.slnx -c Release
dotnet test ErpConnect.slnx -c Release

# Contrat : régénérer le client TypeScript et l'instantané après toute modification de ErpConnect.Contracts
dotnet run --project tools/ErpConnect.Tools.ContractGen

# Interface
cd ui/desktop
npm ci
npm run lint; npm run typecheck; npm test -- --run
npm run tauri dev        # application de bureau (Windows, moteur installé)

# Coquille Rust
cargo test --manifest-path ui/desktop/src-tauri/Cargo.toml --workspace

# Service de développement (PowerShell administrateur, Windows)
.\tools\dev\Install-DevService.ps1
.\tools\dev\Test-DpapiVirtualAccount.ps1
.\tools\dev\Uninstall-DevService.ps1 [-RemoveData]
```

Documentation : [architecture](docs/architecture/vue-ensemble.md), [protocole RPC](docs/architecture/protocole-rpc.md),
[note DSI](docs/security/guide-dsi.md), [rapports de phase](docs/phases/).

## Sécurité

Aucun secret, certificat privé ou fichier de licence ne doit être commité. Voir [SECURITY.md](SECURITY.md).

## Licence

Logiciel propriétaire. Tous droits réservés.
