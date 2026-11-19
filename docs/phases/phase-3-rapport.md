# Rapport de Phase 3 – Connecteurs et noyau d'exécution par lots

> Version 0.3.0, protocole JSON-RPC 1.1. Développé et testé sous Linux (conteneur) ; les parties propres à Windows
> héritées de la Phase 2 restent à valider sur Windows (voir le rapport de Phase 2).

## Objectif et critères (dossier de validation, section 9)

| Critère | Résultat |
|---|---|
| SDK de connecteur + suite de conformance | Fait. `ErpConnect.Connectors.Sdk` + suites abstraites `FormatConformanceTests` et `TransportConformanceTests`. |
| CSV, XLSX, JSON, XML | Fait, chaque format passe la conformance (aller-retour, vide, invalide, annulation, 200 000 lignes en flux). |
| Dossiers locaux, SFTP, REST | Fait. SFTP testé contre un **vrai serveur OpenSSH** lancé par les tests ; REST contre un vrai serveur HTTP local. |
| Noyau d'exécution manuelle par lots | Fait : lots, point de reprise par lot, compteurs, rejets, journal, annulation après le lot en cours. |
| 1 M lignes CSV→REST sous 500 Mo | **Mesuré : 5,7 s, pic 263 Mo** (pic du processus de test entier). |
| 1 M lignes SFTP→JSON sous 500 Mo | **Mesuré : 6,4 s, pic 253 Mo.** |

## Fichiers créés ou modifiés

- `src/ErpConnect.Connectors.Sdk/` : format pivot `Record` (types autorisés, profondeur ≤ 32), manifeste, schéma de configuration
  avec champs secrets, `IConnector`, `IRecordReader` (lots + curseur), `IRecordWriter` (résultat par enregistrement, `CompleteAsync`),
  `IFileTransport` (écriture atomique), `IRecordFormat`, `SafePath` (anti-traversée), `ConnectorException` transitoire/permanente,
  `RetryPolicy` (exponentielle + gigue, Retry-After), `RequestRateLimiter`, `RecordJson`.
- `src/ErpConnect.Connectors/` : formats CSV (RFC 4180 en flux, UTF-8/Windows-1252), JSON (tableau ou lignes), XML (DTD interdites),
  XLSX (MiniExcel, contrôle anti-bombe zip) ; transports dossier local et SFTP (SSH.NET) ; connecteurs `file.local`, `file.sftp`, `rest`.
- `src/ErpConnect.Flows/` : définition de flux versionnée (JSON), validation, correspondance des champs (Phase 3 : chemins de champs).
- `src/ErpConnect.Core/Flows/Models.cs` : connexions, flux, versions, exécutions, rejets, journal, interfaces de dépôts.
- `src/ErpConnect.Storage/` : migrations `config/0002_connections_flows.sql` (versions de flux immuables par trigger),
  `runs/0002_progress_rejects.sql` ; dépôts `ConnectionRepository`, `FlowRepository`, `RunRepository`.
- `src/ErpConnect.Engine/` : `ConnectionService` (secrets au coffre), `FlowService` (versions, publication), `RunService` (noyau).
- `src/ErpConnect.Contracts/` : 13 méthodes et 1 notification (`runs.changed`), DTO ; contrat TypeScript régénéré.
- `src/ErpConnect.Service/` : branchement RPC, composition, relais de la notification.
- `ui/desktop/src/screens/` : écrans **Connexions**, **Flux** (source / correspondance / cible), **Exécutions** (détail, rejets, journal, annulation).
- Tests : `ErpConnect.Connectors.Tests` (91), `ErpConnect.Engine.Tests` (13 + 2 performance), `FlowRpcTests` (4), mises à jour S3/contrat.
- CI : nouveau job Linux avec OpenSSH (tests SFTP réels) ; le job Windows exclut `Category=Sftp`.

## Ce qui fonctionne réellement (développé ET testé)

- **Formats** : aller-retour sans perte (séparateurs, guillemets, retours ligne, accents, japonais) ; entrée invalide → erreur permanente ;
  annulation respectée ; lecture en flux (tas < 64 Mo pour 200 000 lignes, < 200 Mo pour XLSX).
- **Sécurité des fichiers** : XXE et « milliard de rires » refusés ; JSON trop profond refusé ; champ CSV > 1 Mio refusé ;
  bombe zip XLSX refusée ; types non pris en charge refusés.
- **Transports** : liste par motif, lecture, écriture atomique (échec = rien de visible), `CreateNew` refuse un fichier existant,
  `Overwrite` remplace, déplacement/suppression, traversée de chemin refusée (`..`, absolu, lecteur, caractère nul),
  liens symboliques sortant de la racine refusés (local).
- **SFTP** : empreinte de clé d'hôte obligatoire et vérifiée (mauvaise clé = connexion refusée), authentification par clé,
  création des dossiers, renommage atomique.
- **REST** : pagination par lien suivant (OData compatible), boucle de pagination détectée, lien vers un autre hôte refusé
  (le jeton ne part pas), HTTP clair refusé sauf option, identifiants dans l'URL refusés, 429 + Retry-After respectés,
  5xx réessayés puis classés transitoires, 4xx permanents non réessayés, résultat par enregistrement, clé d'idempotence stable,
  envoi par lot, authentification basic/bearer/clé d'API depuis les secrets, limiteur de débit.
- **Noyau** : compteurs exacts, rejet = Échec (règle métier), cible fichier publiée seulement si tout est bon (tout ou rien),
  erreurs transitoires réessayées par enregistrement, annulation effective après le lot en cours, exécutions interrompues
  passées en Suspendu au redémarrage, source vide = Réussi avec avertissements, connexion manquante = Échec propre.
- **Connexions** : secrets uniquement au coffre (vérifié en cherchant le secret dans le fichier `config.db`), jamais renvoyés
  par le RPC ni écrits dans l'audit ; secret conservé si non resaisi, effacement explicite ; suppression refusée si un flux l'utilise.
- **Flux** : chaque enregistrement crée une version immuable (trigger SQLite), concurrence optimiste, publication ;
  l'exécution normale utilise la version publiée, le test d'un brouillon exige `flows.edit`.
- **Droits** : matrice S3 étendue aux 13 nouvelles méthodes pour les 5 rôles.
- **Interface** : 3 écrans, 10 nouveaux tests Vitest (secret jamais pré-rempli, envoyé dans `secrets` seulement, erreurs de champ,
  publication/exécution, droits opérateur, rafraîchissement sur notification).

## Tests réalisés et résultats (Linux, ce conteneur)

| Suite | Résultat |
|---|---|
| .NET hors performance (11 projets) | 279 réussis, 7 ignorés (Windows uniquement), 0 échec |
| Performance 1 M lignes (2 tests) | 2 réussis (263 Mo et 253 Mo de pic) |
| Vitest | 29 réussis |
| ESLint, `tsc --noEmit`, `vite build` | propres |

## Ce qui n'est PAS testé

- Exécution sous Windows des nouveaux composants (la CI Windows les exécutera ; les chemins UNC et les jonctions NTFS ne sont pas testés).
- SFTP avec authentification par mot de passe (le serveur de test n'accepte que la clé) et clés RSA/PPK.
- REST en HTTPS réel (les tests utilisent HTTP local avec `allowHttp`) ; aucune API ERP réelle.
- Interface Tauri réelle avec ces écrans (testés en jsdom uniquement).

## Problèmes identifiés

- Correspondance limitée aux chemins de champs : le langage d'expressions arrive en Phase 4.
- La reprise d'une exécution existe dans le noyau (point de reprise enregistré) mais aucune méthode RPC ne la déclenche encore (Phase 5).
- Une cible REST n'est pas transactionnelle : en cas d'échec, les enregistrements déjà envoyés restent envoyés (c'est indiqué dans le journal).
- Le curseur des fichiers repose sur l'index d'enregistrement : la reprise relit le fichier depuis le début pour sauter les lignes déjà traitées.
- La purge selon les durées de rétention n'est toujours pas faite.

## Risques

- MiniExcel (XLSX) charge la table des chaînes partagées en mémoire : un classeur avec des millions de chaînes distinctes peut dépasser le budget.
- Le motif de fichiers ne parcourt pas les sous-dossiers (choix volontaire, à confirmer).
- `HttpListener` est utilisé par les tests : sous Windows il exige un compte administrateur (le cas des runners GitHub).

## Procédure de validation

1. `dotnet test ErpConnect.slnx` sous Windows (les tests SFTP sont exclus par `--filter "Category!=Sftp"`), puis sous Linux avec OpenSSH.
2. Créer une connexion « Dossier local » vers `C:\ERPConnect\in` et une vers `C:\ERPConnect\out`, déposer un CSV, créer le flux,
   le tester, le publier, l'exécuter ; vérifier le fichier JSON produit et le détail de l'exécution.
3. Ajouter une ligne qui provoque un rejet : vérifier le statut Échec et l'absence de fichier publié.
4. Vérifier dans `config.db` (outil SQLite) qu'aucun mot de passe n'apparaît en clair.
