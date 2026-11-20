# Rapport de Phase 2 – Socle Windows

Date : 19 novembre 2026. Environnement de développement : conteneur **Linux** (Ubuntu 24.04, SDK .NET 10.0.115,
Node 22, Rust 1.97). Aucune machine Windows n'était disponible : tout ce qui dépend de Windows est écrit, compilé
(C# et vérification Rust `--target x86_64-pc-windows-gnu`), mais **non exécuté**.

## Choix par défaut retenus pour les points ouverts (section 9 du dossier de validation)

| Point | Choix retenu |
| --- | --- |
| Nom du service | `ERPConnectEngine`, compte `NT SERVICE\ERPConnectEngine` |
| Licences d'un Control Center hors éditeur | Clé de signature chez l'éditeur ; licences déjà signées ou certificat délégué (Phase 7) |
| Durées de conservation | 13 mois (396 j), 90 j, 30 j, minimum 7 j, paramétrables |
| Groupes locaux | Un par rôle ; `BUILTIN\Administrateurs` reçoit le rôle Administrateur au premier démarrage |
| Désinstallation | Données conservées par défaut (`-RemoveData` pour tout supprimer) |
| Export des secrets | Phase 8 ; d'ici là, pas de restauration des secrets sur une autre machine |
| Offre GitHub | Non tranché par moi : décision et coût de votre côté (Pro ou Team pour protéger `main` en privé) |

## Écarts par rapport au plan (à valider)

1. **JSON-RPC écrit dans le projet** au lieu de StreamJsonRpc (ADR 0014, proposé).
2. **SQL via ADO.NET** (Microsoft.Data.Sqlite seul) au lieu de Dapper : une dépendance de moins, SQL toujours écrit à la main.
3. **Vérification du serveur par le propriétaire du pipe** plutôt que par le jeton du processus serveur (ADR 0015, proposé).
4. **`net10.0` au lieu de `net10.0-windows`** : le code Windows est isolé et annoté, ce qui permet de tester le reste sur
   n'importe quel système. Le produit reste Windows uniquement (le service refuse de démarrer ailleurs).
5. **Installateur MSI non livré** dans cette phase : remplacé pour l'instant par les scripts `tools/dev/*.ps1`.
6. **Menus** : les 11 menus sont nommés d'après le dossier ; les numéros de phase affichés pour Exécutions,
   Planification, Supervision et Scripts sont mes hypothèses.

## Fichiers créés ou modifiés

- Solution `ErpConnect.slnx`, `Directory.Build.props`, `Directory.Packages.props` (versions centralisées, fichiers de verrouillage).
- `src/` : Core, Contracts (+ `contract.snapshot.json`), Security, Storage (+ migrations SQL), Rpc, Engine, Service.
- `tools/ErpConnect.Tools.ContractGen`, `tools/dev/*.ps1` (installation, désinstallation, comptes de test, test DPAPI).
- `tests/` : 8 projets de test (unitaires, contrat, architecture, intégration, sécurité).
- `ui/packages/contracts` (généré), `ui/desktop` (React + Vite + TanStack Query), `ui/desktop/src-tauri` (coquille + crate `relay`).
- `.github/CODEOWNERS` (@AlecLe59), `.github/workflows/ci.yml` (build de l'interface avant Tauri, contrat à jour, workspace Rust).
- Documentation : `docs/architecture/*`, `docs/security/guide-dsi.md`, ADR 0014 et 0015, ce rapport, README.

## Ce qui fonctionne réellement (développé ET testé ici)

| Fonction | Preuve |
| --- | --- |
| Machine à états des 7 statuts, les 147 combinaisons (de, vers, acteur) | `RunStateMachineTests` |
| Matrice rôles × permissions conforme au tableau | `PermissionTests` |
| Migrations SQLite : base vide, rejeu, refus d'une migration modifiée (S16), refus d'une base plus récente, sauvegarde avant migration et conservation des 5 dernières, annulation d'une migration en échec | `MigrationRunnerTests`, `RobustnessTests.S16` |
| Coffre AES-256-GCM : chiffrement, falsification d'un octet détectée, déplacement ou changement d'usage refusé (S10), rotation de clé, redémarrage | `SecretCipherTests`, `SecretVaultTests`, `SecretLeakTests.S10` |
| Journal d'audit en ajout seul et chaîné ; modification ou suppression détectée (S11) | `AuditHasherTests`, `SecretLeakTests.S11` |
| Masquage des secrets dans les journaux et exceptions (S9), secret témoin introuvable sur disque (S8, hors Journal d'événements) | `SecretRedactorTests`, `SecretLeakTests` |
| Protocole JSON-RPC : session.hello obligatoire, versions, erreurs propres, 4 Mo, 1 000 niveaux, fuzzing (S12) | `FramingTests`, `DispatcherTests`, `RobustnessTests.S12` |
| Serveur de pipe réel (socket Unix sous Linux) : limite de connexions, 1 000 connexions en boucle (S13), inactivité | `PipeServerTests`, `RobustnessTests.S13` |
| Moteur complet par le pipe : état réel, paramètres validés et versionnés conservés après redémarrage, rôles, restriction Opérateur, dernier Administrateur (S14), notifications | `EngineIntegrationTests` |
| Matrice complète méthode × rôle, chaque refus audité (S3, hors ACL Windows) | `PermissionMatrixTests` |
| Interface : 11 menus filtrés par permissions, écrans Tableau de bord, Utilisateurs et droits, Paramètres, Journal d'audit, « moteur indisponible » et reconnexion, refus et incompatibilité de version | 19 tests Vitest |
| Relais Rust : format des méthodes, découpage, corrélation d'appels concurrents, délais, déconnexion ; configuration Tauri (S15 : une seule commande, aucun plugin, CSP stricte) | 11 tests cargo |
| Contrat C# → TypeScript généré et vérifié en CI | `ContractSnapshotTests` |

## Tests réalisés et résultats

| Suite | Résultat |
| --- | --- |
| `dotnet test ErpConnect.slnx -c Release` | **144 réussis, 0 échec, 7 ignorés** (tests Windows) |
| `npm run lint`, `npm run typecheck`, `npm test -- --run` | 0 erreur, **19 tests réussis** |
| `npm audit --audit-level=high --omit=dev` | 0 vulnérabilité |
| `cargo fmt --check`, `cargo clippy --workspace --all-targets -D warnings`, `cargo test --workspace` | 0 avertissement, **11 tests réussis** |
| `cargo clippy --target x86_64-pc-windows-gnu` (crate relay) | compile sans avertissement (non exécuté) |

## Ce qui n'est PAS testé (Windows requis)

| Élément | État |
| --- | --- |
| Service Windows réellement installé, redémarrage après arrêt forcé, arrêt < 30 s | Code et script écrits, non exécutés |
| ACL du pipe (S1), refus réseau (S2), faux serveur (S4), ports (S5), compte et privilèges (S6), ACL des dossiers (S7) | Tests écrits (`WindowsSecurityTests`), ignorés hors Windows |
| **DPAPI sur compte virtuel** (point à vérifier en tout premier) | Script `Test-DpapiVirtualAccount.ps1` prêt, non exécuté |
| Lecture du jeton du client (`RunAsClient`), résolution de comptes AD | Non exécuté |
| Vérification du propriétaire du pipe par le relais Rust | Compilé pour Windows, non exécuté |
| Application Tauri lancée réellement avec WebView2 | Non lancée (compilée sous Linux uniquement) |
| Journal d'événements Windows (S8 partiel) | Non vérifié |
| MSI WiX | Non développé |

## Problèmes identifiés

1. **Groupes AD et ACL du pipe** : l'ACL n'autorise que les groupes locaux ERP Connect et les Administrateurs. Un
   groupe AD qui reçoit un rôle dans ERP Connect doit aussi être membre d'un groupe local ERP Connect, sinon ses
   membres sont bloqués par l'ACL. À trancher : documenter cette règle, ou élargir l'ACL aux utilisateurs
   interactifs en laissant `session.hello` refuser les comptes sans rôle.
2. Le SID de service « restreint » peut interagir avec DPAPI en contexte utilisateur : c'est précisément ce que
   le test DPAPI doit établir.
3. La purge par rétention n'est pas développée (le journal d'audit est en ajout seul ; la purge exigera une ancre de chaîne).

## Risques techniques

- DPAPI sur compte virtuel (repli prévu : DPAPI machine + ACL de `keys` + entropie).
- Scripts PowerShell et code Windows jamais exécutés : des corrections sont probables au premier passage sur Windows.
- La CI Windows exécutera pour la première fois `WindowsFact` (S4, ACL du pipe) : un échec est possible et serait à corriger, pas à désactiver.

## Procédure de validation (sur Windows 11 puis Windows Server 2022)

1. `dotnet test ErpConnect.slnx -c Release` : tout doit passer, y compris S4 et le test d'ACL du pipe.
2. PowerShell administrateur : `.\tools\dev\Install-DevService.ps1`, puis `.\tools\dev\Test-DpapiVirtualAccount.ps1`
   (service Running à chaque étape, même empreinte de clé, aucune erreur DPAPI). Relancer après redémarrage de la
   machine et après une mise à jour Windows.
3. `.\tools\dev\New-TestAccounts.ps1` puis, dans la même session, `dotnet test tests\ErpConnect.SecurityTests -c Release` : S1, S2, S5, S6, S7 doivent passer.
4. `cd ui\desktop; npm ci; npm run tauri dev` : l'application affiche l'état réel du moteur et votre compte Windows.
   Arrêter le service : bandeau « Moteur indisponible » ; le redémarrer : reconnexion seule.
5. Attribuer le rôle Opérateur à un groupe, se connecter avec un membre : menus restreints.
6. Modifier un paramètre, redémarrer service puis machine : la valeur est conservée.
7. `.\tools\dev\Uninstall-DevService.ps1` : service, binaires et groupes supprimés, données conservées.

## Reste à faire pour clore la Phase 2

Exécuter la procédure ci-dessus, conclure le test DPAPI par un ADR, corriger ce qui échoue sous Windows,
développer le MSI WiX (mise à jour 0.2.0 → 0.2.1 avec migration, désinstallation, WebView2).
