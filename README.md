# ERP CONNECT

Logiciel d'intégration universelle de données pour Windows, installé chez le client.

**Principe :** SOURCE → TRANSFORMATION → DESTINATION

> **État du projet : Phase 1 (conception) terminée. Phase 2 (socle Windows) non démarrée.**
> Aucune fonctionnalité n'est opérationnelle à ce jour. Ce dépôt ne contient encore que sa structure,
> sa documentation et ses règles de contribution. Chaque fonctionnalité ne sera déclarée opérationnelle
> qu'une fois développée, testée et documentée.

## Architecture

| Composant | Technologie | Dossier |
| --- | --- | --- |
| Application desktop | Tauri 2 + React + TypeScript | `ui/desktop` |
| Moteur d'intégration | .NET 10 LTS | `src/ErpConnect.Engine` |
| Service Windows | .NET 10 Worker Service | `src/ErpConnect.Service` |
| Communication locale | JSON-RPC 2.0 sur named pipe (ACL) | `src/ErpConnect.Rpc` |
| Stockage local | SQLite (WAL) | `src/ErpConnect.Storage` |
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

Les commandes de build seront documentées ici à la livraison de la Phase 2.

## Sécurité

Aucun secret, certificat privé ou fichier de licence ne doit être commité. Voir [SECURITY.md](SECURITY.md).

## Licence

Logiciel propriétaire. Tous droits réservés.
