# Revue de sécurité – Phase 6 (version 0.6.0)

> Revue interne faite par l'équipe de développement, **pas un audit indépendant ni un test d'intrusion**.
> Chaque contrôle renvoie au test qui le vérifie. Ce qui n'a pas de test est marqué « non vérifié ».
> Environnement : Linux (conteneur) ; les contrôles propres à Windows restent non vérifiés sur une machine réelle.

## 1. Surface d'attaque

| Entrée | Qui peut l'atteindre | Protection principale |
|---|---|---|
| Named pipe `ERPConnect.Engine` | Comptes Windows locaux membres d'un groupe ERP Connect | ACL du pipe, refus du SID NETWORK, rôle obligatoire, droits par méthode |
| Fichiers lus (CSV, XLSX, JSON, XML, ZIP) | Quiconque dépose un fichier dans un dossier source | Analyseurs en flux, limites de taille et de profondeur, DTD interdites, anti-bombe zip |
| Réponses de serveurs distants (REST, SFTP, FTPS) | Le serveur distant ou un intermédiaire réseau | TLS validé ou empreinte épinglée, clé d'hôte SSH obligatoire, pagination bornée au même hôte |
| Bases SQLite source | Quiconque contrôle le fichier | Ouverture en lecture seule, une seule instruction, identifiants entre guillemets |
| **Scripts JavaScript (nouveau)** | Concepteurs (`flows.edit`), si l'administrateur a activé les scripts | Bac à sable Jint, voir section 3 |
| Expressions de correspondance | Concepteurs | Langage maison sans accès système (interpréteur, pas de `eval`) |

Aucun port réseau entrant (ADR 0010). Aucune mise à jour automatique, aucune télémétrie.

## 2. Contrôles existants (Phases 2 à 5) et leur preuve

| Contrôle | Preuve | État |
|---|---|---|
| Droits par méthode pour les 5 rôles, refus audités | `PermissionMatrixTests.S3_*` (toutes les méthodes × 5 rôles) | Vérifié (Linux) |
| Compte sans rôle : aucune session | `S3_account_without_role_cannot_even_open_a_session` | Vérifié |
| Messages bornés (4 Mio, profondeur 64), 32 connexions max | `ErpConnect.Rpc.Tests`, `S13_thousand_connections_in_a_loop` | Vérifié (S13 instable sous charge sur Windows CI, voir problèmes) |
| ACL du pipe, refus réseau, propriétaire du serveur de pipe | Tests `[WindowsOnly]` (7 ignorés sous Linux) | **Non vérifié** |
| Secrets chiffrés AES-256-GCM, jamais renvoyés, jamais audités | `SecretLeakTests`, recherche du secret dans `config.db` | Vérifié |
| Clé maître protégée par DPAPI | Protecteur de test sous Linux | **Non vérifié** (DPAPI réel) |
| Journal d'audit chaîné SHA-256 | Tests de falsification de `ChainedAuditLog` | Vérifié |
| Traversée de chemin, liens symboliques sortant de la racine | Conformance des transports | Vérifié (jonctions NTFS **non vérifiées**) |
| XXE, « milliard de rires », bombe zip, JSON profond | `ErpConnect.Connectors.Tests` | Vérifié |
| SFTP : clé d'hôte obligatoire ; FTPS : TLS obligatoire sauf autorisation | Serveurs réels OpenSSH et pyftpdlib dans les tests | Vérifié |
| REST : pas de jeton envoyé à un autre hôte, HTTP clair refusé | Tests REST | Vérifié (HTTPS réel **non vérifié**) |
| Aucun secret dans le dépôt | gitleaks sur tout l'historique (CI) | Vérifié |

## 3. Bac à sable des scripts (nouveau en Phase 6)

Moteur : **Jint 4.18** (interpréteur JavaScript écrit en .NET, dans le processus du service).

| Menace | Mesure | Test |
|---|---|---|
| Accès au système (.NET, fichiers, processus) | Interop désactivée (`Interop.Enabled = false`, pas d'`AllowClr`), aucun objet hôte injecté ; l'enregistrement traverse en JSON | `No_host_object_is_reachable` (System, importNamespace, clr, require, process, fetch, XMLHttpRequest, Deno), `Record_is_plain_json_without_clr_type_access` |
| Code dynamique | `DisableStringCompilation` : `eval`, `new Function`, `constructor('…')` refusés | `Dynamic_code_and_host_timers_are_unavailable` |
| Modules | Chargement de modules désactivé (statique et `import()`) | `Import_and_modules_are_unavailable` |
| Boucle infinie | Délai par enregistrement (500 ms) + plafond d'instructions (1 000 000) | `Infinite_loop_is_stopped`, `Statement_flood_is_stopped`, `Infinite_loop_at_top_level_is_refused_at_compile` |
| Épuisement mémoire | Plafond d'allocation 32 Mio par enregistrement, tableaux ≤ 100 000 éléments | `Memory_bomb_is_stopped`, `Huge_array_is_stopped` |
| Récursion | Profondeur d'appel ≤ 64 | `Deep_recursion_is_stopped` |
| Expression régulière catastrophique (ReDoS) | Délai des expressions régulières = délai par enregistrement | `Catastrophic_regex_is_stopped` |
| Débordement de pile de l'analyseur (source imbriquée) | Source ≤ 64 Kio ; `InsufficientExecutionStackException` interceptée et refusée | `Deeply_nested_source_does_not_crash_the_process` (trouvé pendant cette revue, corrigé) |
| Résultat hostile (profondeur, types) | Retour JSON relu avec profondeur ≤ 32, objets uniquement | `Too_deep_result_is_a_reject`, `Non_object_results_are_rejects` |
| Pollution entre flux | Un interpréteur par bloc et par exécution | `Prototype_pollution_stays_inside_the_sandbox` |
| Journal inondé | Motif de rejet tronqué à 300 caractères | `Script_error_message_is_truncated` |
| Concepteur malveillant ou compte volé | Scripts **désactivés par défaut** ; activation réservée à `settings.update` (Administrateur), auditée ; désactiver bloque aussi l'exécution des flux déjà enregistrés | `Scripts_are_disabled_by_default_and_the_switch_is_audited` |

Un dépassement de limite **arrête l'exécution** (erreur permanente) au lieu de rejeter l'enregistrement : un script
défaillant ne consomme pas le temps de tout un fichier. Une erreur levée par le script (`throw`) est un rejet normal.

### Limites assumées du bac à sable

- **Même processus que le service.** Jint est un interpréteur géré : il n'exécute pas de code natif et n'a pas d'accès
  système sans interop, mais une faille de Jint lui-même s'exécuterait avec les droits du compte de service (qui sont
  minimaux, ADR 0006). Un isolement par processus (AppContainer + Job Object) est prévu pour Python et n'est **pas fait**.
- La limite mémoire de Jint compte les **allocations** du thread, pas la mémoire vivante : elle est prudente (elle
  arrête tôt) mais ne protège pas contre un pic du ramasse-miettes du processus.
- Les variables globales d'un script persistent d'un enregistrement à l'autre pendant une exécution (utile pour un
  compteur ; documenté). Elles ne persistent pas entre exécutions.
- Le délai est mesuré par appel ; un flux de 1 million d'enregistrements avec un script lent peut durer longtemps.
  L'annulation (`runs.cancel`) agit après le lot en cours.
- `Date.now()` et `Math.random()` restent disponibles : un script n'est donc pas forcément déterministe.

## 4. Constats de la revue

| # | Constat | Gravité | Suite donnée |
|---|---|---|---|
| R1 | Une source JavaScript très imbriquée faisait remonter `InsufficientExecutionStackException`, non interceptée (erreur interne au lieu d'un refus propre) | Moyenne | Corrigé et testé |
| R2 | Le résultat d'un bloc script était ignoré par le pipeline (seuls les blocs qui modifient en place étaient pris en compte) | Fonctionnelle | Corrigé, test de chaînage ajouté |
| R3 | Les contrôles Windows (ACL du pipe, DPAPI, compte virtuel, ACL des dossiers) n'ont jamais tourné sur une machine Windows réelle | Élevée tant que non vérifié | Reste à faire : procédure dans le rapport de Phase 2 |
| R4 | `S13_thousand_connections_in_a_loop` a échoué une fois en CI Windows (délai de connexion de 5 s) | Faible (disponibilité) | À surveiller ; si cela se reproduit, analyser la boucle d'acceptation du serveur de pipe |
| R5 | Aucun test d'intrusion ni revue externe | — | À planifier avant une mise en production |
| R6 | Python isolé non développé | — | Reporté (ADR 0008) |
| R7 | La purge selon les durées de rétention n'est pas faite : les rejets (qui contiennent des données métier) s'accumulent | Moyenne (RGPD) | Reste à faire |

## 5. Dépendances tierces exécutant des données non fiables

MiniExcel (XLSX), SSH.NET, FluentFTP, Microsoft.Data.Sqlite, Cronos, Jint. Toutes épinglées par fichier de verrouillage
(`--locked-mode`) ; la CI échoue sur un paquet vulnérable connu (`dotnet list package --vulnerable`) et la revue des
dépendances GitHub s'applique aux PR.
