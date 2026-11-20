# Architecture technique (état Phase 2)

```
┌──────────────── Poste ou serveur Windows ────────────────┐
│  ERP Connect Desktop (Tauri 2)                            │
│   React + TypeScript ──invoke('engine_call')──► Rust      │
│                                       relais seul (ADR 3) │
│                                          │ JSON-RPC 2.0   │
│                                          ▼ \\.\pipe\ERPConnect.Engine (ACL)
│  Service ERPConnectEngine (NT SERVICE\ERPConnectEngine)   │
│   Rpc → Engine → Storage (SQLite) / Security (coffre)     │
│   %ProgramData%\ERPConnect\{data,keys,logs,backup}        │
└───────────────────────────────────────────────────────────┘
          Aucun port réseau ouvert (ADR 0010)
```

## Projets .NET et sens des dépendances

| Projet | Rôle | Dépend de |
| --- | --- | --- |
| ErpConnect.Core | Statuts et machine à états, rôles et permissions, paramètres, interfaces | — |
| ErpConnect.Contracts | Contrat JSON-RPC (méthodes, DTO, erreurs, version 1.0) | — |
| ErpConnect.Security | Coffre AES-256-GCM, protection DPAPI, identité Windows, masquage, empreintes d'audit | Core |
| ErpConnect.Storage | SQLite, migrations SQL intégrées, dépôts | Core |
| ErpConnect.Rpc | Découpage des messages, routage, serveur de pipe, ACL Windows | Core, Contracts, Security |
| ErpConnect.Engine | Services métier de la Phase 2 (état, paramètres, droits, audit) | Core, Storage, Security |
| ErpConnect.Service | Hébergement du service Windows et assemblage | tous |
| ErpConnect.Tools.ContractGen | Génération du client TypeScript et de l'instantané du contrat | Contracts |

Le test `ArchitectureTests.Dependencies_only_point_towards_core` fait échouer la CI si ce tableau n'est pas respecté.

## Démarrage du service

1. Création des dossiers ; migrations de `config.db`, `runs.db`, `audit.db` (sauvegarde préalable de toute base
   existante, 5 conservées ; refus si une migration appliquée a été modifiée ou si la base est plus récente).
2. Installation : identifiant et entropie DPAPI créés au premier démarrage.
3. Coffre : clé maître v1 créée au premier démarrage, puis déprotégée à chaque démarrage (échec = arrêt).
4. Droits : au premier démarrage, `BUILTIN\Administrateurs` reçoit le rôle Administrateur (audité).
5. Pipe : première instance créée avec `FirstPipeInstance` ; un pipe déjà existant arrête le service (S4).

Toute erreur de démarrage est journalisée en critique et le processus sort avec un code non nul ; les actions de
récupération du service (1, 5, 15 min) s'appliquent.

## Stockage

Trois bases SQLite en WAL, clés étrangères actives, attente de verrou 5 s, `synchronous=FULL` pour `audit.db`.
Le journal d'audit est en ajout seul (déclencheurs SQL) et chaîné par SHA-256.
