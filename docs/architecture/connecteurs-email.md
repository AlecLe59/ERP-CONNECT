# Connecteurs e-mail : IMAP et SMTP

Deux usages courants dans une PME : **récupérer les fichiers reçus par courriel** (factures fournisseurs, relevés, commandes
en CSV ou XLSX) et **envoyer par courriel un fichier produit par un flux** (export pour le comptable, état quotidien).
Les deux connecteurs réutilisent les formats de fichiers existants (CSV, JSON, XML, XLSX, ZIP) : une pièce jointe se lit
comme un fichier, un fichier produit part comme une pièce jointe.

## Connexion (commune)

| Champ | Valeurs | Remarque |
| --- | --- | --- |
| `host`, `port` | IMAP 993, SMTP 587 par défaut | |
| `security` | `ssl` (défaut IMAP), `starttls` (défaut SMTP), `none` | `none` est refusé sauf si `allowPlain=true` |
| `certificateFingerprint` | empreinte SHA-256 | Pour un serveur à certificat auto-signé ; vide = validation par le système |
| `auth` | `password` (défaut), `oauth2` | `oauth2` : flux « client credentials » (Microsoft 365, Google Workspace), jeton envoyé en XOAUTH2 |
| `username`, `password` | | Mot de passe (ou mot de passe d'application) dans le coffre chiffré |
| `tokenUrl`, `clientId`, `clientSecret`, `scope` | | OAuth2 ; `tokenUrl` en HTTPS obligatoire |

Erreurs : identifiants refusés, certificat refusé, dossier absent, adresse refusée (5xx SMTP) = **permanente** ;
serveur injoignable, coupure, refus temporaire (4xx SMTP) = **transitoire** (le moteur réessaie).

## Lire les pièces jointes (`mail.imap`)

- **Ressource** : un motif de nom de pièce jointe, par exemple `*.csv` ou `releve-*.xlsx`.
- **Options de la source** : `from` (l'expéditeur contient), `subject` (l'objet contient), `sinceDays` (30 par défaut, 0 = tout),
  `unseenOnly` (seulement les non lus), `folder` (sinon celui de la connexion, `INBOX` par défaut), plus les options du format
  (`format=csv`, `delimiter=;`…).
- Chaque pièce jointe retenue devient un fichier nommé `UID-partie-nom` ; les fichiers sont lus dans l'ordre d'arrivée des messages,
  et la reprise après une coupure repart de la pièce jointe où elle s'était arrêtée.
- **Lecture seule** : le dossier est ouvert en lecture seule et les pièces jointes sont lues sans marquer les messages comme lus.
  L'aperçu d'un flux ne change rien à la boîte.
- **Après traitement** (`afterRead`) :
  - `none` (défaut) : rien ne change. Pour ne pas traiter deux fois la même ligne d'une exécution à l'autre, utiliser la
    clé anti-doublon du flux (par exemple le numéro de facture) ou `sinceDays`.
  - `seen` : les messages lus sont marqués comme lus (à combiner avec `unseenOnly=true`).
  - `move` avec `moveTo=Traité` : les messages lus sont déplacés dans ce dossier (qui doit exister).
  Le marquage n'a lieu **qu'après une exécution réussie sans rejet, une fois la cible écrite**. En cas d'échec ou de rejet,
  les messages restent en place et seront relus. Si le marquage lui-même échoue, les données restent écrites et l'exécution
  se termine « avec avertissement ». Seuls les messages qui avaient une pièce jointe retenue sont marqués ou déplacés.

## Envoyer un fichier (`mail.smtp`)

- **Connexion** : `from` (expéditeur), `to` (destinataires, séparés par des virgules), `cc`, `subject` et `body` par défaut,
  `sendWhenEmpty` (faux par défaut). Les destinataires sont fixés dans la connexion : un flux ne peut pas en ajouter.
- **Ressource** : le nom du fichier joint, avec `{date}`, `{timestamp}`, `{runId}` (par exemple `clients-{date}.csv`).
- **Options de la cible** : le format (`format=csv`, `xlsx`…), `subject` et `body` (avec `{fichier}`, `{date}`, `{runId}`).
- **Un courriel par exécution**, envoyé seulement quand le fichier est entièrement produit. Une exécution sans aucune ligne
  n'envoie rien (sauf `sendWhenEmpty=true`), une exécution en échec non plus. L'objet ne peut pas injecter d'en-tête
  (retours à la ligne neutralisés) ; un en-tête `X-ErpConnect-Run` donne l'exécution d'origine.
- Limite connue : si la connexion est coupée juste après l'acceptation du message par le serveur, la reprise peut l'envoyer
  une seconde fois (SMTP ne permet pas de le savoir).

## Ce qui a été testé, et où

| Domaine | Testé | Comment |
| --- | --- | --- |
| IMAP : filtres, motif, ordre, lecture seule, `seen`, `move`, erreurs | Oui, sous Linux | Vrai serveur IMAP/SMTP (GreenMail 2.1.5) en TLS avec certificat épinglé ; certificat non épinglé, mauvaise empreinte, mot de passe faux, port fermé, dossier absent, connexion en clair refusée |
| SMTP : pièce jointe, copie, objet, rien sans ligne ni après abandon, erreurs | Oui, sous Linux | Même serveur ; le courriel reçu est relu en IMAP et sa pièce jointe vérifiée |
| Flux complets par le moteur | Oui, sous Linux | Boîte → CSV avec déplacement des messages (aperçu sans effet, deuxième exécution vide) ; rejet = messages laissés en place ; échec du déplacement = avertissement ; CSV → courriel |
| OAuth2 (Microsoft 365, Google) | **Non** | Le jeton et XOAUTH2 sont codés selon la documentation des fournisseurs, sans appel réel |
| Fournisseurs réels | **Non** | Ni Microsoft 365, ni Exchange sur site, ni Gmail, ni OVH n'ont été appelés |
| Windows | **Non** | Validation des certificats par le magasin Windows non testée |
