# Audit final – Phases 2 à 8

> État au 19/11/2026, version 0.8.0, branche `claude/project-thread-1stpeq` (PR #6).
> Règle appliquée partout : « fait » veut dire **développé et testé**. Tout le développement et les tests ont eu lieu
> sous **Linux (conteneur)**. La CI GitHub exécute aussi la suite .NET sous Windows, mais **aucune installation réelle
> sur Windows** (service, MSI, DPAPI du compte de service, application Tauri) n'a été faite.

## 1. Synthèse

| | |
|---|---|
| Tests .NET (11 projets, performance comprise) | 474 réussis, 7 ignorés (Windows uniquement), 0 échec sous Linux |
| Tests interface (Vitest) | 47 réussis ; ESLint et `tsc` propres |
| Contrôles | `restore --locked-mode` OK, gitleaks propre (aucun secret dans le dépôt) |
| CI Windows | Phases 6 et 7 : un seul échec, `S13_thousand_connections_in_a_loop` (délai de connexion au pipe). Cause trouvée et corrigée (voir 4.1) ; **la correction attend la confirmation de la CI**. |
| Prêt pour la production ? | **Non.** Le moteur est fonctionnellement riche et bien testé, mais la mise en service Windows (MSI, comptes, ACL, DPAPI) n'a jamais été exécutée. |

## 2. Audit par phase

### Phase 2 – Socle Windows (moteur .NET 10, pipe JSON-RPC, SQLite, coffre, interface Tauri)

- **Fonctionne (testé)** : moteur, protocole JSON-RPC sur named pipe (limites de taille, d'inactivité, de connexions), rôles et permissions
  (matrice S3 sur toutes les méthodes), trois bases SQLite avec migrations vérifiées par empreinte, coffre AES-256-GCM, journal d'audit chaîné,
  interface React (jsdom), relais Rust (compilé, testé sous Linux).
- **Non testé** : service Windows installé, ACL du pipe et des dossiers, refus réseau, comptes et privilèges (tests écrits, ignorés hors Windows),
  **DPAPI sur compte virtuel** (point le plus important), lecture du jeton client, application Tauri avec WebView2.
- **Problèmes** : un groupe AD doit être membre d'un groupe local ERP Connect pour passer l'ACL du pipe (règle à trancher).
- **Risques** : le code Windows n'a jamais tourné sur une vraie machine ; des corrections sont probables au premier passage.

### Phase 3 – Connecteurs et noyau d'exécution

- **Fonctionne (testé)** : SDK et suites de conformance ; CSV, XLSX, JSON, XML ; dossiers locaux, SFTP (vrai serveur OpenSSH), REST (vrai serveur HTTP) ;
  lots, points de reprise, rejets, annulation. 1 M lignes CSV→REST en 5,7 s (pic 263 Mo), SFTP→JSON en 6,4 s (253 Mo).
- **Non testé** : chemins UNC et jonctions NTFS, SFTP par mot de passe ou clés RSA/PPK, REST en HTTPS réel, aucune API ERP réelle.
- **Problèmes** : une cible REST n'est pas transactionnelle ; la reprise sur fichier relit depuis le début.
- **Risques** : XLSX garde les chaînes partagées en mémoire (classeurs énormes).

### Phase 4 – Expressions, transformations, aperçu, ZIP, FTPS

- **Fonctionne (testé)** : langage d'expressions maison (38 fonctions, sans `eval`), blocs filtre/calcul/renommage/suppression/contrôle,
  aperçu sans écriture (200 enregistrements), ZIP protégé (zip-slip, bombe), FTPS avec épinglage de certificat (vrai serveur pyftpdlib).
- **Non testé** : serveurs FTP du marché, FTPS implicite, validation de certificat par la chaîne Windows ; ergonomie de l'éditeur.
- **Problèmes** : découverte limitée (pas de schéma REST) ; éditeur sous forme de formulaire, sans glisser-déposer ; remplacement FTP non atomique.
- **Risques** : l'aperçu montre des données réelles aux concepteurs (voulu, à confirmer).

### Phase 5 – Planification, reprise, anti-doublons, alertes, SQLite

- **Fonctionne (testé)** : cron + fuseau + heure d'été, pas de chevauchement, rattrapage unique, nouvelles tentatives sur erreur transitoire,
  `runs.resume`, clé anti-doublon, alertes et écran Supervision, connecteur SQLite. Trois défauts du noyau corrigés (point de reprise, 503 compté en rejet, délai).
- **Non fait** : connecteurs **SQL Server, PostgreSQL, Oracle, ODBC** ; **alertes par e-mail** (SMTP/M365) et IMAP.
- **Non testé** : planificateur dans le vrai service, fuseaux Windows.
- **Problèmes** : livraison « au moins une fois » ; pagination SQL instable sans `ORDER BY`.

### Phase 6 – Scripts JavaScript isolés

- **Fonctionne (testé)** : bloc script Jint avec limites (temps, mémoire, instructions, récursion, taille), aucun accès .NET ;
  29 tests de scripts malveillants ; désactivé par défaut, activable par l'Administrateur seul, vérifié à l'enregistrement, à l'aperçu, à l'essai et à l'exécution.
  Revue de sécurité interne rédigée.
- **Non fait** : **Python isolé** (AppContainer + Job Object).
- **Problèmes** : le script tourne dans le processus du service ; limite mémoire en allocations ; `Date.now()`/`Math.random()` non déterministes.
- **Risques** : la sécurité dépend de Jint (suivre ses correctifs).

### Phase 7 – Licences et supervision

- **Fonctionne (testé)** : licence ECDSA P-256 hors ligne liée à l'installation, évaluation 30 jours, droits (exécution, planification, scripts, plafond de flux),
  import/retrait audités, outil éditeur (`keygen`, `issue`, `verify`). Aucune clé dans le dépôt.
- **Partiel** : supervision par le journal des événements Windows (code écrit, non vérifié sur Windows).
- **Non fait** : **agent distant** (HTTPS sortant + mTLS) et **Control Center** (multi-organisations).
- **Problèmes** : reculer l'horloge prolonge une évaluation ou une licence expirée ; pas de délai de grâce ; pas de liste de révocation.

### Phase 8 – Sauvegarde, restauration, rétention, installeur

- **Fonctionne (testé)** : sauvegarde à chaud cohérente avec manifeste SHA-256 ; export des clés Argon2id + AES-GCM avec phrase de passe ;
  restauration vérifiée avant écriture, sur la même machine et sur une autre (secrets relus, audit valide, licence conservée) ; refus propres
  (phrase fausse, archive altérée, bombe) ; purge selon la rétention toutes les 6 h, auditée.
- **Ébauche non construite** : **MSI WiX** et script des groupes locaux.
- **Non testé** : commande `restore` réelle (DPAPI machine, saisie masquée), restauration d'un schéma antérieur.
- **Problèmes** : pas de sauvegarde planifiée ni de copie hors machine, pas de purge des archives, pas de retour arrière automatique.

## 3. Ce qui reste à faire (par priorité)

1. **Passage sur Windows** (bloquant) : construire et installer le MSI, exécuter les procédures des rapports 2 et 8,
   conclure le test DPAPI sur compte virtuel, faire passer les tests `WindowsFact` sur une machine réelle, lancer l'application Tauri.
2. Confirmer en CI la correction de S13.
3. Connecteurs bases de données manquants (SQL Server, PostgreSQL au minimum), avec un serveur de test réel.
4. Alertes par e-mail.
5. Protection contre le recul d'horloge (mémoriser la date la plus récente vue).
6. Sauvegardes planifiées, copie hors machine, purge des archives.
7. Python isolé, agent distant et Control Center (chantiers à part entière, à planifier).

## 4. Points de vigilance transverses

### 4.1 S13 sous Windows (corrigé, à confirmer)
Sous Windows, un client qui se déconnecte avant que le serveur accepte la connexion provoque une `IOException` ;
l'instance de pipe n'était pas libérée. Les instances perdues s'accumulaient jusqu'au plafond (32) et plus aucun client ne pouvait se connecter.
Correctif dans `PipeServer` : l'instance est libérée dans ce cas. Le test n'a pas été modifié. Ce cas ne se reproduit pas sous Linux :
seule la CI Windows peut le confirmer. En production, ce défaut aurait pu rendre le moteur injoignable après des déconnexions brutales.

### 4.2 Sécurité
- Aucun secret dans le dépôt (gitleaks) ; tests avec secrets générés à l'exécution.
- Les droits du compte de service et les ACL des dossiers conditionnent toute la sécurité : c'est précisément la partie non testée.
- Revue de sécurité interne (Phase 6), **pas d'audit externe**.

### 4.3 Écart avec la cible initiale
Non faits : agent distant, Control Center, Python isolé, connecteurs SQL Server/PostgreSQL/Oracle/ODBC, e-mail des alertes, MSI construit.
