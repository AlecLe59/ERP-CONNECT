# Rapport de Phase 7 – Licences et supervision

> Version 0.7.0, protocole JSON-RPC 1.5 (ajouts compatibles 1.x). Développé et testé sous Linux (conteneur).
> Cette phase couvre la **licence hors ligne** et les **droits d'usage** dans le moteur. L'agent distant (ADR 0011)
> et le Control Center (ADR 0012) **ne sont pas développés**.

## Objectif et critères

| Critère | Résultat |
|---|---|
| Licence signée | Fait : ECDSA P-256 / SHA-256 (signature IEEE P1363), charge utile JSON (client, édition, installation, dates, fonctions, plafond de flux publiés). |
| Activation hors ligne | Fait : l'écran Licence affiche l'identifiant d'installation ; l'éditeur signe une licence liée à cet identifiant ; l'administrateur l'importe (fichier ou texte collé). Aucun appel réseau. |
| Droits d'usage | Fait : exécution (manuelle et planifiée), planification (`scheduler`), scripts (`scripts`), plafond de flux publiés ; code RPC `ENTITLEMENT_DENIED` (-32011). |
| Évaluation | Fait : 30 jours complets depuis l'installation ; ensuite exécutions et nouvelles publications bloquées. Lecture, historique, audit et retrait de licence restent possibles. |
| Outil de l'éditeur | Fait : `tools/ErpConnect.Tools.License` (`keygen`, `issue`, `verify`), essayé à la main (licence valide acceptée, charge utile modifiée refusée). |
| Supervision | **Partiel** : chaque alerte est aussi écrite dans le journal du service et, en service Windows, dans le journal des événements (niveau Erreur/Avertissement) pour les outils de supervision existants. Écran Supervision depuis la Phase 5. |
| Agent distant HTTPS sortant + mTLS | **Non fait.** |
| Control Center (ASP.NET Core + PostgreSQL, multi-organisations) | **Non fait.** |

## Gestion de la clé de l'éditeur (aucun secret dans le dépôt)

- Le moteur ne contient **que des clés publiques**. La clé de l'éditeur est fournie à la compilation :
  `dotnet build -p:ErpConnectLicensePublicKey=<base64>` (métadonnée d'assemblage de `ErpConnect.Security`).
- **Le dépôt ne contient aucune clé** : une compilation ordinaire ne peut vérifier aucune licence et reste en évaluation ;
  l'écran Licence le dit et désactive l'import (vérifié par un test).
- La clé privée est créée par `keygen` sur le poste de l'éditeur (droits 600 sous Linux) et ne doit jamais entrer dans le dépôt.
- Les tests génèrent une paire de clés à chaque exécution.

## Fichiers créés ou modifiés

- `src/ErpConnect.Security/Licensing/License.cs` : `LicensePayload`, `LicenseFile`, `LicenseVerifier` (vérification, signature pour l'outil).
- `src/ErpConnect.Core/Licensing/Entitlements.cs` : `Features`, `IEntitlements`, `ILicenseStore`.
- `src/ErpConnect.Storage/` : migration `config/0004_license.sql`, `LicenseRepository`.
- `src/ErpConnect.Engine/LicenseService.cs` : état, contrôle des droits, import et retrait audités ; `EngineErrorKind.Entitlement`.
- `RunService` (exécution, déclencheurs planifiés), `FlowService` (publication), politique des scripts, `SchedulerService`
  (une nouvelle tentative refusée lève une alerte au lieu de boucler).
- `src/ErpConnect.Contracts/DtosPhase7.cs`, méthodes `license.get` (`dashboard.read`), `license.import` et `license.remove` (`license.manage`), protocole 1.5.
- `src/ErpConnect.Service/` : `RegisterLicense`, branchement, alertes vers le journal (`EngineWorker`).
- `tools/ErpConnect.Tools.License/` (nouveau, ajouté à la solution).
- `ui/desktop/src/screens/License.tsx` (nouveau) ; plus aucun menu « Disponible en Phase ».
- Tests : `LicenseVerifierTests` (14), `LicenseTests` (7), `LicenseRpcTests` (2), `License.test.tsx` (3), matrice S3 (+3 méthodes), App.test.

## Ce qui fonctionne réellement (développé ET testé)

- Signature : licence valide relue à l'identique ; signée par une autre clé, modifiée (installation, plafond, date), format DER,
  fichier illisible ou trop gros, clé non P-256 : tous refusés.
- Évaluation : exécution et publication possibles pendant 30 jours, refusées ensuite avec un message clair ; données toujours lisibles.
- Import : licence d'une autre installation ou contrefaite refusée et **auditée en échec** ; licence valide rétablit les exécutions,
  auditée sans recopier la signature ; retrait audité.
- Licence expirée : exécutions refusées. Plafond de flux : la publication d'un nouveau flux est refusée, republier un flux déjà publié reste possible.
- Planification sans la fonction : aucune exécution, une alerte « lancement planifié impossible ». Exécution manuelle toujours possible.
- Par le pipe : `ENTITLEMENT_DENIED` après l'évaluation, puis droit rétabli après import.

## Tests réalisés et résultats (Linux, ce conteneur)

| Suite | Résultat |
|---|---|
| .NET, toutes suites (11 projets, performance comprise) | 450 réussis, 7 ignorés (Windows uniquement), 0 échec |
| Vitest | 45 réussis |
| ESLint, `tsc --noEmit`, `restore --locked-mode` | propres |

## Ce qui n'est PAS testé

- Une compilation de publication avec une vraie clé d'éditeur (seulement des clés générées par les tests et un essai manuel de l'outil).
- L'écriture des alertes dans le journal des événements Windows (dépend de la source créée par l'installateur, Phase 8).

## Problèmes identifiés

- **Retour en arrière de l'horloge** : un administrateur qui recule l'horloge de la machine prolonge l'évaluation ou une licence expirée.
  Aucune protection (une parade serait de mémoriser la date la plus récente vue).
- La reprise sur place d'une exécution Suspendue (arrêt du service) n'est pas soumise à la licence : elle termine un travail commencé.
- Pas de délai de grâce après expiration : le blocage est immédiat.
- Un administrateur de la machine peut toujours modifier les binaires : la licence protège contre l'usage ordinaire, pas contre un attaquant local.

## Risques

- Si la clé privée de l'éditeur fuit, toutes les licences peuvent être contrefaites ; le moteur accepte plusieurs clés publiques
  (rotation possible) mais aucune liste de révocation n'existe.
- L'absence d'agent et de Control Center limite la supervision à distance aux outils qui lisent le journal des événements Windows.

## Procédure de validation

1. Sur le poste de l'éditeur : `dotnet run --project tools/ErpConnect.Tools.License -- keygen C:\coffre\licence.pem` ; noter la clé publique.
2. Compiler le service avec `-p:ErpConnectLicensePublicKey=<clé publique>` et l'installer.
3. Écran Licence : relever l'identifiant d'installation. Chez l'éditeur :
   `... -- issue C:\coffre\licence.pem <identifiant> "Client" Standard 2027-12-31 scheduler,scripts unlimited client.lic`.
4. Importer `client.lic` : état « Licence active » ; vérifier l'entrée `license.import` dans le journal d'audit.
5. Modifier un caractère du fichier et l'importer : refus « Signature de licence invalide ».
6. Avec une licence sans `scheduler`, vérifier qu'une planification lève une alerte et n'exécute rien ; vérifier l'alerte dans l'Observateur d'événements.
