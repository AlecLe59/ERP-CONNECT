# Rapport de Phase 4 – Éditeur de flux, expressions, aperçu, ZIP, FTP/FTPS

> Version 0.4.0, protocole JSON-RPC 1.2 (ajouts compatibles avec 1.x). Développé et testé sous Linux (conteneur) ;
> rien de propre à Windows n'a été ajouté dans cette phase.

## Objectif et critères

| Critère | Résultat |
|---|---|
| Langage d'expressions sûr pour la correspondance | Fait. Compilateur maison (pas de `eval`, pas de script) : 38 fonctions, limites de taille/profondeur/texte, regex à 100 ms. Voir `docs/architecture/expressions.md`. |
| Blocs de transformation | Fait : `filter`, `set`, `rename`, `remove`, `validate`, appliqués avant la correspondance. |
| Aperçu sans écriture | Fait : `flows.preview` lit au plus 200 enregistrements de la source, applique blocs et correspondance, n'écrit rien et ne crée aucune exécution. |
| Découverte de la structure | Fait pour les connecteurs fichiers (liste des fichiers) via `connections.discover` ; les champs sont affichés par l'aperçu. |
| Archives ZIP (lecture et écriture) | Fait, avec protections zip-slip, bombe de compression, 10 000 entrées / 2 Gio max. |
| FTP/FTPS | Fait (FluentFTP) : FTPS explicite ou implicite, épinglage du certificat par empreinte SHA-256, FTP clair refusé sauf autorisation explicite. Testé contre un **vrai serveur FTPS** (pyftpdlib, TLS obligatoire). |
| Éditeur trois zones | Fait : Source / Transformation (blocs + correspondance + aide des fonctions) / Cible, bouton Aperçu. |

## Fichiers créés ou modifiés

- `src/ErpConnect.Flows/Expressions.cs`, `Functions.cs` : analyseur lexical et syntaxique, arbre évalué, bibliothèque de fonctions.
- `src/ErpConnect.Flows/FlowDefinition.cs` : `StepSpec`, `Steps` (validés à l'enregistrement, chemins d'erreur `steps[i].champ`).
- `src/ErpConnect.Flows/Mapper.cs` : `RecordPipeline` (blocs puis correspondance), sort de chaque enregistrement (`Output`, `Filtered`, `Rejected`).
- `src/ErpConnect.Engine/RunService.cs` : utilise le pipeline ; les filtrés sont comptés dans `SkippedCount` (affiché « filtré(s) » dans le journal).
- `src/ErpConnect.Engine/PreviewService.cs` (nouveau), `ConnectionService.DiscoverAsync`.
- `src/ErpConnect.Connectors/Archives.cs` (nouveau), `FileConnector.cs` (options `archive`, `entryPattern`, `entryName` ; connecteur `file.ftp`),
  `Transports/FtpTransport.cs` (nouveau).
- Contrat : `StepSpecDto`, `FlowPreviewParams/Result`, `PreviewRowDto`, `FunctionDto`, `ConnectionDiscoverParams/Result` ;
  méthodes `connections.discover` (droit `connections.use`), `flows.preview` (`flows.edit`), `flows.functions` (`flows.read`).
  Instantané et types TypeScript régénérés.
- UI : `screens/Flows.tsx` (`StepsEditor`, `PreviewPanel`, aide des fonctions, option Archive), tests associés.
- CI : le job Linux installe pyftpdlib + pyOpenSSL (versions figées) ; le job Windows exclut `Category=Ftp` comme `Category=Sftp`.
- Version 0.4.0 (`Directory.Build.props`, `package.json`, `CLIENT_VERSION`).

## Ce qui fonctionne réellement (développé ET testé)

- Expressions : priorités, logique, comparaisons, 20 fonctions vérifiées une à une, décimaux exacts (`0.1 + 0.2 = 0.3`),
  bascule exacte en décimal sur dépassement d'entier, propagation de `null`, erreurs de syntaxe signalées à la compilation,
  erreurs d'exécution transformées en rejets, limites (longueur, profondeur, 1 Mio de texte), regex catastrophique interrompue.
- Blocs : ordre d'application, source jamais modifiée, filtre ≠ rejet, motif de rejet personnalisé, validation des blocs invalides.
- Noyau : un flux CSV→JSON avec filtre + calcul + correspondance donne exactement 10 lus / 5 écrits / 5 filtrés ;
  un contrôle en échec met l'exécution en Échec, enregistre le rejet (étape `mapping`, motif, index) et ne publie aucun fichier.
- ZIP : lecture de plusieurs archives et entrées dans l'ordre, reprise au milieu d'une archive, écriture atomique d'un ZIP,
  zip-slip refusé en lecture et en écriture, bombe (64 Mio de zéros dans < 1 Mio) refusée avant décompression, archive corrompue = erreur permanente.
  Flux ZIP (CSV) → ZIP (JSON) de bout en bout.
- FTPS : la suite de conformance des transports (écriture atomique, liste, lecture, CreateNew/Overwrite, chemins hors racine refusés,
  déplacement, suppression, fichier absent) passe contre le serveur réel ; certificat épinglé accepté ; empreinte fausse,
  certificat auto-signé sans épinglage, mot de passe faux refusés ; FTP clair refusé côté client sans autorisation.
- RPC par le pipe : découverte, liste des fonctions, aperçu (sorts, sortie JSON, aucun fichier écrit), aperçu invalide → `-32005`,
  aller-retour des blocs par `flows.save`/`flows.get` ; matrice des droits S3 étendue aux 3 nouvelles méthodes (5 rôles).
- UI (jsdom) : ajout, modification, réordonnancement et enregistrement des blocs ; panneau d'aperçu ; option ZIP.

## Tests réalisés et résultats (Linux, ce conteneur)

| Suite | Résultat |
|---|---|
| .NET complet (11 projets, y compris performance, SFTP et FTPS) | 349 réussis, 7 ignorés (Windows uniquement), 0 échec |
| dont nouveaux : expressions/blocs 58, ZIP 7, FTPS 20, noyau Phase 4 5, RPC 2 | tous réussis |
| Vitest | 32 réussis |
| ESLint, `tsc --noEmit` | propres |

## Ce qui n'est PAS testé

- Exécution sous Windows des nouveaux composants (assurée par la CI Windows, hors tests FTP).
- FTPS contre un serveur réel du marché (FileZilla Server, IIS FTP) et FTPS implicite (le serveur de test est en explicite).
- Certificat FTPS validé par la chaîne de confiance Windows (seul l'épinglage est testé).
- Interface Tauri réelle avec le nouvel éditeur (jsdom uniquement) ; ergonomie non validée par un utilisateur.

## Problèmes identifiés

- Découverte : pour les fichiers, seule la liste des fichiers est renvoyée ; les champs viennent de l'aperçu. Pour REST, rien (pas de schéma OpenAPI).
- L'éditeur visuel reste un formulaire (pas de glisser-déposer entre champs source et cible) ; les champs source s'affichent après un aperçu.
- FTP n'a pas de renommage atomique avec remplacement : en mode « Remplacer », la cible est supprimée juste avant le renommage
  (fenêtre très courte sans fichier).
- Lire un ZIP sur SFTP/FTP le télécharge d'abord dans un fichier temporaire local (une fois par entrée lue).
- Les filtrés réutilisent la colonne « ignorés » (`SkippedCount`) : pas de migration, mais le libellé DTO reste `skippedCount`.

## Risques

- `flows.preview` montre des données réelles de la source à un utilisateur ayant `flows.edit` ; c'est voulu (concepteur de flux) mais à confirmer.
- Le délai regex (100 ms) multiplié par des millions d'enregistrements peut ralentir fortement un flux mal écrit (pas de blocage, mais lenteur).
- pyftpdlib est un serveur de test : le comportement de serveurs FTP anciens (mode actif, listes non standard) peut différer.

## Procédure de validation

1. `dotnet test ErpConnect.slnx --filter "Category!=Sftp&Category!=Ftp"` sous Windows ; sous Linux, installer `openssh-server`
   et `pip install pyftpdlib pyopenssl` puis `dotnet test ErpConnect.slnx`.
2. Dans l'éditeur : flux CSV → JSON, ajouter un filtre `[pays] = 'FR'`, un calcul `set ttc = round(toNumber([ht], ',') * 1.2, 2)`,
   un contrôle `not isEmpty([email])`, cliquer **Aperçu** : vérifier les lignes Transmis / Filtré / Rejeté, puis tester et vérifier les compteurs.
3. Déposer un ZIP de CSV, source avec Archive = ZIP ; cible JSON en Archive = ZIP ; vérifier le contenu produit.
4. Connexion FTPS vers un serveur interne avec l'empreinte SHA-256 du certificat ; vérifier qu'une empreinte fausse est refusée.
