# Connecteurs de bases de données

Cinq connecteurs partagent le même fonctionnement (`src/ErpConnect.Connectors/Sql/`) :

| Connecteur | Identifiant | Pilote | Usage typique |
| --- | --- | --- | --- |
| PostgreSQL | `db.postgresql` | Npgsql 10 | Applications web, ERP récents |
| SQL Server | `db.sqlserver` | Microsoft.Data.SqlClient 7 | Sage 100 SQL, FRP 1000, la plupart des ERP Windows |
| MySQL / MariaDB | `db.mysql` | MySqlConnector 2 | Sites e-commerce, applications web |
| Oracle | `db.oracle` | Oracle.ManagedDataAccess.Core 23 | Grands ERP |
| ODBC | `db.odbc` | Pilote ODBC installé sur la machine | Toute autre base (Sage ancien format, Access, HFSQL…) |

## Lire

La ressource d'une source est soit **un nom de table** (`clients`, `dbo.clients`), soit **une requête SELECT** unique.

- **Table** : lue triée par sa clé primaire ; la reprise après une coupure repart exactement après la dernière clé lue.
  Sans clé primaire sur une seule colonne, indiquer la colonne de tri dans l'option `key`. En ODBC, `key` est toujours nécessaire.
- **Requête** : lue telle quelle ; la reprise saute les lignes déjà lues, donc la requête doit avoir un `ORDER BY` stable.

**Lecture seule.** Une lecture se fait toujours dans une transaction qui est annulée à la fin. PostgreSQL, MySQL et Oracle
la mettent en plus en lecture seule côté serveur (`SET TRANSACTION READ ONLY`), et toute écriture y est refusée.
SQL Server et ODBC n'ont pas cet équivalent : une écriture glissée dans la requête est annulée avec la transaction, mais
**la seule vraie garantie est un compte de base de données sans droit d'écriture**, recommandé pour toute source.

## Écrire

La ressource d'une destination est un nom de table. Options :

- `mode=insert` (défaut) : insertion ; une ligne déjà présente est rejetée.
- `mode=upsert` avec `key=col1,col2` : mise à jour si la clé existe, insertion sinon (`ON CONFLICT` PostgreSQL,
  `ON DUPLICATE KEY` MySQL, `MERGE` SQL Server et Oracle). Relancer le même fichier ne crée donc pas de doublon.
  Non disponible en ODBC.

Chaque lot est d'abord envoyé en un seul aller-retour. Si une ligne est refusée (contrainte, type, valeur manquante), le lot est
annulé puis repris ligne par ligne : seules les lignes fautives sont rejetées, avec le message du serveur, les autres sont écrites.
Oracle n'a pas d'envoi groupé dans son pilote : il passe toujours ligne par ligne, plus lentement.

## Connexion et sécurité

- Le mot de passe est un champ secret (coffre chiffré), jamais réaffiché. En ODBC, un mot de passe placé dans la chaîne de
  connexion est refusé : il doit aller dans le champ prévu.
- Le chiffrement est **obligatoire et vérifié par défaut** (`tls=require`, certificat du serveur contrôlé). « Accepter un
  certificat non vérifié » n'est à activer que sur un réseau maîtrisé.
- SQL Server accepte l'authentification Windows du compte du service (`integratedSecurity=true`).
- Les noms de tables et de colonnes sont échappés selon le moteur ; les valeurs sont toujours passées en paramètres.
- Erreurs : serveur injoignable, délai, interblocage = **transitoire** (le moteur réessaie) ; mot de passe faux, table absente,
  contrainte = **permanente**. Exception ODBC : les pilotes renvoient le même code pour un serveur injoignable et un mot de passe
  faux ; l'échec de connexion est donc permanent, pour ne pas verrouiller un compte à force de réessayer.

## Ce qui a été testé, et où

| Domaine | Testé | Comment |
| --- | --- | --- |
| Les cinq connecteurs | Oui, sous Linux | Vrais serveurs : PostgreSQL 16, SQL Server 2022, MySQL 8.4, Oracle 23 Free, ODBC avec psqlODBC vers PostgreSQL. 11 cas communs par moteur (connexion, mot de passe faux, serveur injoignable, découverte, lecture et reprise, requête, lecture seule, échappement, table sans clé, rejets ligne par ligne, upsert) |
| Flux complets par le moteur | Oui, sous Linux | CSV → base (upsert, relancé deux fois) → JSON et CSV, pour PostgreSQL, SQL Server, MySQL, Oracle |
| Volumes | PostgreSQL seulement | 1 million de lignes lues et écrites en CSV en 2,4 s ; 100 000 lignes CSV insérées en 2,2 s (conteneur Linux, 4 cœurs) |
| Chiffrement | Partiel | SQL Server et MySQL testés avec TLS obligatoire (certificat du serveur de test accepté sans vérification) ; vérification complète du certificat et TLS Oracle (TCPS) non testés |
| Windows | **Non** | Authentification Windows SQL Server, pilotes ODBC Windows, Sage 100 et FRP 1000 réels : à faire sur une vraie machine |

En CI, le job Linux démarre ces serveurs à chaque exécution (`ERPCONNECT_REQUIRE_DB=1` : un serveur absent fait échouer les tests
au lieu de les ignorer). Sous Windows, ces tests sont ignorés.
