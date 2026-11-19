# Rapport de Phase 8 – Sauvegarde, restauration, rétention, installeur, documentation

> Version 0.8.0, protocole JSON-RPC 1.6 (ajouts compatibles 1.x). Développé et testé sous Linux (conteneur).
> L'installeur MSI est une **ébauche non construite et non testée**.

## Objectif et critères

| Critère | Résultat |
|---|---|
| Sauvegarde | Fait : à chaud, cohérente (`VACUUM INTO`), trois bases + manifeste (versions de schéma, tailles, SHA-256), RPC `backup.create` / `backup.list`, panneau dans Paramètres. |
| Export des clés Argon2id | Fait : avec phrase de passe (12 caractères min.), clés maîtres chiffrées Argon2id (64 Mio, 3 passes, 2 voies) + AES-256-GCM, paramètres liés par données associées, bornés à l'import. |
| Restauration | Fait : demande déposée par `ErpConnect.Service.exe restore <archive>` (service arrêté), exécutée par le service à son démarrage sous son propre compte ; vérification complète avant écriture ; état précédent conservé ; audit succès/échec. |
| Restauration sur une autre machine | Fait et testé (protecteur de clé différent) : secrets relus, chaîne d'audit valide, identifiant d'installation (et donc licence) conservé. Sans export des clés : refus, ou `--discard-secrets` (secrets à ressaisir). |
| Purge selon la rétention | Fait (manquait depuis la Phase 2) : journaux, rejets, clés anti-doublons, en-têtes, alertes acquittées ; toutes les 6 h ; auditée. |
| Installeur MSI (WiX) | **Ébauche** : `installer/wix/Package.wxs` + script des groupes locaux. Jamais construit (pas de WiX ni de Windows ici). |
| Documentation | Fait : [guide d'exploitation](../exploitation/guide-exploitation.md), note DSI mise à jour, README de l'installeur, ce rapport et l'[audit final](audit-final.md). |

## Fichiers créés ou modifiés

- `src/ErpConnect.Security/Backup/KeyEscrow.cs` (Argon2id via `Konscious.Security.Cryptography.Argon2` 1.3.1) ; `MasterKeyStore.Import`.
- `src/ErpConnect.Engine/BackupService.cs` : création, liste, restauration statique, demande de restauration.
- `src/ErpConnect.Engine/RetentionService.cs` ; `RunRepository.PurgeAsync` ; `IRetentionStore`, `RetentionCutoffs`, `PurgeCounts`.
- `src/ErpConnect.Service/EngineRuntime.cs` : traitement de la demande de restauration au démarrage, audit de la restauration, services branchés.
- `src/ErpConnect.Service/Program.cs` : commande `restore` (vérifie l'archive et la phrase, protège la phrase par DPAPI machine).
- `src/ErpConnect.Contracts/DtosPhase8.cs`, méthodes `backup.create` (`settings.edit`, phrase marquée `[Secret]`) et `backup.list` (`settings.read`).
- `ui/desktop/src/components/BackupPanel.tsx`, intégré à Paramètres.
- `installer/` : `wix/Package.wxs`, `scripts/New-ErpConnectGroups.ps1`, `README.md`.
- Tests : `KeyEscrowTests` (15), `BackupRestoreTests` (6), `RetentionTests` (3), Vitest (+2), matrice S3 (+2 méthodes).

## Ce qui fonctionne réellement (développé ET testé)

- Export des clés : aller-retour ; ni clé ni phrase en clair dans le fichier ; phrase fausse, chiffré/étiquette/sel modifiés refusés ;
  baisser le coût Argon2 dans le fichier rend l'export invalide ; paramètres démesurés (4 Gio, 1000 passes, 64 voies) refusés **avant** dérivation.
- Restauration sur une autre machine avec phrase de passe : mot de passe de connexion relu, audit vérifié, historique revenu, état de B conservé.
- Sans clés : refus propre (rien ne change, échec audité), puis `--discard-secrets` : connexions restaurées sans secret.
- Même machine sans clés : secrets relus avec les clés locales.
- Phrase fausse, base altérée (empreinte), entrée plus grosse qu'annoncé (anti-bombe) : refus avant toute modification.
- RPC : phrase trop courte refusée, liste des sauvegardes, phrase absente du journal d'audit, contenu exact de l'archive.
- Purge : chaque durée s'applique à ses données ; exécution en cours jamais purgée ; parent d'une reprise conservée gardé ; purge auditée.

## Défauts trouvés et corrigés pendant cette phase

1. La première version jugeait une clé locale « disponible » sur son seul numéro : sur une autre machine, la clé v1 locale (différente)
   aurait été gardée et les secrets seraient devenus illisibles. Désormais une clé n'est acceptée que si elle **déchiffre réellement** un secret de la sauvegarde.
2. `--discard-secrets` laissait les fichiers de clés locales : le coffre refusait de démarrer (« clés sans référence »). Ils sont maintenant mis de côté.

## Tests réalisés et résultats (Linux, ce conteneur)

| Suite | Résultat |
|---|---|
| .NET, toutes suites (11 projets, performance comprise) | 474 réussis, 7 ignorés (Windows uniquement), 0 échec |
| Vitest | 47 réussis |
| ESLint, `tsc --noEmit`, `restore --locked-mode`, gitleaks | propres |

## Ce qui n'est PAS testé

- La commande `restore` réelle (Windows uniquement : DPAPI machine, saisie masquée) ; les tests déposent la demande directement.
- DPAPI réel du compte de service pour les clés restaurées.
- Le MSI : ni construit, ni installé, ni désinstallé ; ACL des dossiers, groupes locaux, source d'événements non vérifiés.
- Restauration d'une sauvegarde d'une version **antérieure** du schéma (la migration au démarrage devrait s'appliquer ; non testé).

## Problèmes identifiés

- Pas de sauvegarde planifiée ni de copie hors machine automatique.
- Pas de retour arrière automatique d'une restauration : l'état précédent est conservé dans `backup\pre-restore-*`, à remettre à la main.
- La phrase de passe transite en clair dans la requête RPC locale (pipe protégé par ACL) ; elle n'est ni journalisée ni conservée.
- Les sauvegardes s'accumulent dans `backup\` : aucune purge des archives.

## Risques

- Une archive avec export des clés n'est protégée que par la phrase de passe : une phrase faible expose tous les secrets.
- Le MSI non testé est le principal risque de mise en service : droits des dossiers et compte du service conditionnent toute la sécurité.

## Procédure de validation

1. Construire le MSI (installer/README.md), installer sur un Windows Server de test, faire les 6 vérifications du README.
2. Créer une connexion avec mot de passe ; Paramètres → Sauvegardes, cocher « Inclure les clés », saisir une phrase, sauvegarder.
3. Sur une deuxième machine : installer, arrêter le service, `ErpConnect.Service.exe restore <archive>`, saisir la phrase, démarrer ;
   tester la connexion (le mot de passe doit fonctionner) ; vérifier `backup.restore` et l'intégrité dans le Journal d'audit.
4. Recommencer avec une archive sans clés : refus attendu ; puis avec `--discard-secrets`.
5. Régler la rétention des journaux réussis à 7 jours, attendre une purge (6 h ou redémarrage) : entrée `retention.purge` dans l'audit.
