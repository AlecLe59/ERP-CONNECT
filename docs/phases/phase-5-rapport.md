# Rapport de Phase 5 – Planification, reprise, anti-doublons, alertes, connecteur base de données

> Version 0.5.0, protocole JSON-RPC 1.3 (ajouts compatibles 1.x). Développé et testé sous Linux (conteneur).
> Le service Windows démarre le planificateur, mais ce démarrage n'est pas testé hors Windows.

## Objectif et critères

| Critère | Résultat |
|---|---|
| Planification | Fait : une planification par flux, cron standard à 5 champs (Cronos 0.13.0), fuseau IANA ou Windows, heure d'été gérée, version publiée uniquement. |
| Pas de chevauchement, rattrapage | Fait : une occurrence est sautée (avec alerte « avertissement ») si le flux tourne encore ; après un arrêt, une seule exécution de rattrapage, quel que soit le nombre d'occurrences manquées. |
| Nouvelles tentatives | Fait : uniquement pour une **erreur transitoire**, délai initial × 2 à chaque tentative (plafond 24 h), reprise au point de reprise de l'exécution en échec ; alerte unique quand les tentatives sont épuisées. |
| Reprise | Fait : `runs.resume`. Suspendue (arrêt du service) → reprise sur place, compteurs conservés ; échec transitoire → exécution fille depuis le point de reprise. Échec permanent ou par rejets : refusé. |
| Doublons | Fait : clé anti-doublon par flux (expression) ; registre `DedupKey` (empreinte SHA-256, aucune donnée métier en clair). Cible fichier : clés enregistrées seulement après publication. |
| Limites d'API | Déjà en place depuis la Phase 3 (limiteur de requêtes, 429/Retry-After). |
| Alertes | Fait : échec, aucun enregistrement lu, occurrence sautée, lancement planifié impossible, tentatives épuisées ; notification `alerts.raised` ; écran Supervision avec acquittement tracé dans l'audit. |
| Connecteurs base de données | **Partiel** : SQLite (lecture table/requête, écriture insert/upsert). SQL Server, PostgreSQL, Oracle, ODBC **non faits** (aucun serveur disponible pour les tester honnêtement). |
| E-mail des alertes (SMTP/M365), IMAP | **Non fait** : les alertes restent dans le moteur et l'interface. |

## Défauts du noyau trouvés et corrigés pendant cette phase

1. **Point de reprise effacé** après une erreur de connecteur : la progression enregistrée en échec mettait le point de reprise à vide,
   une reprise serait repartie du début. Désormais seule la dernière progression confirmée est conservée.
2. **Indisponibilité comptée comme rejet métier** : un enregistrement encore en erreur transitoire (503) après les tentatives devenait un rejet ;
   l'exécution échouait « par rejets » et n'était pas reprenable. Désormais c'est une erreur transitoire : le lot n'est pas confirmé et sera renvoyé.
3. **Délai de nouvelle tentative par enregistrement** : il ignorait la fonction d'attente configurée (attente réelle dans les tests).

## Fichiers créés ou modifiés

- Migrations : `config/0003_schedules.sql` (table `Schedule`), `runs/0003_dedup_alerts.sql` (`Run.ErrorKind`, `DedupKey`, `Alert`).
- `src/ErpConnect.Core/Flows/Models.cs` : `ScheduleRecord`, `AlertRecord`, `IScheduleRepository`, `IDedupRegistry`, `IAlertRepository`, `RunRecord.ErrorKind`.
- `src/ErpConnect.Storage/Repositories/ScheduleRepository.cs` (nouveau), `RunRepository.cs` (registre anti-doublons, alertes, nature d'erreur).
- `src/ErpConnect.Engine/SchedulerService.cs` (nouveau), `RunService.cs` (reprise, nature d'erreur, alertes, anti-doublons, correctifs ci-dessus).
- `src/ErpConnect.Connectors/SqliteConnector.cs` (nouveau, `db.sqlite`).
- Contrat : `DtosPhase5.cs`, `FlowDefinitionDto.DedupKey`, `RunDto.ErrorKind` ; méthodes `runs.resume` (flows.run), `schedules.list` (flows.read),
  `schedules.save`/`schedules.delete` (flows.edit), `alerts.list` (dashboard.read), `alerts.acknowledge` (flows.run) ; notification `alerts.raised`.
- Service : le planificateur démarre avec le pipe (tick toutes les 15 s) et s'arrête avant le noyau.
- UI : écrans **Planification** et **Supervision** (alertes en direct), bouton de reprise dans le détail d'exécution, champ « clé anti-doublon ».

## Ce qui fonctionne réellement (développé ET testé)

- Validation (cron, fuseau, flux non publié), calcul de la prochaine occurrence en heure d'hiver, au passage à l'heure d'été, week-end sauté.
- Déclenchement à l'échéance, 72 occurrences manquées → 1 seule exécution, planification désactivée jamais déclenchée,
  l'état du planificateur ne change pas la version de configuration (pas de conflit avec un utilisateur qui modifie).
- Chevauchement sauté avec alerte. Échec transitoire → tentative 1 à +60 s, tentative 2 à +120 s, succès final :
  les 30 enregistrements arrivent, ceux confirmés avant la panne ne sont pas renvoyés. Tentatives épuisées → exactement 1 alerte.
- Arrêt du service pendant une exécution de 600 lignes → Suspendue → reprise sur place → 600 lus / 600 écrits, aucun enregistrement perdu,
  au plus un lot renvoyé (livraison « au moins une fois »).
- Anti-doublons : doublon dans le même fichier ignoré, doublons d'une exécution précédente ignorés, registre propre à chaque flux,
  aucune valeur métier en clair dans `runs.db`, un fichier non publié n'enregistre aucune clé, clé vide = rejet motivé.
- Alertes : création sur échec, notification poussée aux abonnés par le pipe, acquittement unique, tracé dans l'audit.
- SQLite : lecture paginée exacte (reprise), requête SELECT, lecture incapable de modifier la base (lecture seule + requête unique),
  noms de table/colonne échappés (tentatives d'injection sans effet), contrainte violée = rejet de la seule ligne, upsert,
  base verrouillée = erreur transitoire, découverte des tables et colonnes. Flux CSV → SQLite → JSON de bout en bout.
- Matrice des droits S3 étendue aux 6 nouvelles méthodes (5 rôles).

## Tests réalisés et résultats (Linux, ce conteneur)

| Suite | Résultat |
|---|---|
| .NET complet (11 projets, performance, SFTP et FTPS inclus) | 379 réussis, 7 ignorés (Windows uniquement), 0 échec |
| dont nouveaux : moteur Phase 5 17, SQLite 10, RPC 3 | tous réussis |
| Vitest | 38 réussis |
| ESLint, `tsc --noEmit` | propres |

## Ce qui n'est PAS testé

- Le planificateur dans le vrai service Windows (boucle de 15 s), et son comportement au changement d'heure en conditions réelles.
- Les fuseaux horaires Windows (« Romance Standard Time ») : seuls les identifiants IANA sont testés sous Linux.
- SQLite sur un partage réseau (déconseillé par SQLite lui-même).

## Problèmes identifiés

- Une exécution Suspendue par un arrêt du service n'est pas reprise automatiquement par le planificateur : reprise manuelle (bouton) — choix prudent, à valider.
- Livraison « au moins une fois » : le lot interrompu est renvoyé. Pour une API, l'en-tête `Idempotency-Key` le rend sans effet si l'API le gère ;
  sinon, utiliser la clé anti-doublon. Pour SQLite, préférer `upsert`.
- Le registre anti-doublons n'a pas de purge (croissance illimitée) ; la purge globale selon la rétention reste à faire.
- Cible fichier + anti-doublons : si le service s'arrête entre la publication du fichier et l'enregistrement des clés, ces clés seront renvoyées une fois.
- Pagination d'une requête SQL par décalage : sans `ORDER BY` stable, la reprise peut sauter ou répéter des lignes (documenté dans l'aide).

## Risques

- Une requête SQL lente bloque un emplacement d'exécution (4 au total) ; pas encore de délai maximum par requête.
- Le connecteur SQLite accepte un chemin de fichier quelconque : réservé aux administrateurs (`connections.edit`), mais le compte de service doit avoir les droits NTFS.

## Procédure de validation

1. Publier un flux, ouvrir **Planification**, planifier `*/5 * * * *` (Europe/Paris), vérifier « Prochaine exécution », attendre l'exécution (déclencheur « schedule »).
2. Couper la cible (API arrêtée) : vérifier l'échec transitoire, la nouvelle tentative programmée, puis le succès quand la cible revient.
3. Pendant une longue exécution, arrêter le service (`sc stop ERPConnectEngine`), le redémarrer : l'exécution est Suspendue ; cliquer **Reprendre** et vérifier les compteurs.
4. Renseigner une clé anti-doublon `[numero]`, exécuter deux fois le même fichier : la 2e exécution ignore tout.
5. Ouvrir **Supervision** : les alertes apparaissent en direct ; acquitter et vérifier l'entrée d'audit.
