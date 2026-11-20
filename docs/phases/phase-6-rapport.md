# Rapport de Phase 6 – Scripts isolés et revue de sécurité

> Version 0.6.0, protocole JSON-RPC 1.4 (ajouts compatibles 1.x). Développé et testé sous Linux (conteneur).

## Objectif et critères

| Critère | Résultat |
|---|---|
| Mode EXPERT : scripts JavaScript isolés | Fait : bloc « script » (Jint 4.18), `function transform(record)` ; renvoyer l'objet, `null` pour écarter, `throw` pour rejeter. |
| Limites temps, mémoire, récursion, sans accès .NET | Fait : 500 ms, 32 Mio d'allocations, 1 000 000 d'instructions, récursion 64, tableaux ≤ 100 000, source ≤ 64 Kio, par enregistrement. Interop, `eval`, modules désactivés. |
| Tests de scripts malveillants | Fait : 29 tests (boucle infinie, inondation d'instructions, bombe mémoire, tableau géant, récursion, ReDoS, `eval`/`Function`, objets hôtes, modules, source très imbriquée, résultat trop profond, pollution de prototype). |
| Scripts désactivés par défaut (ADR 0008) | Fait : paramètre `scriptsEnabled` (faux par défaut), modifiable par l'Administrateur seul, audité ; vérifié à l'enregistrement, à l'aperçu, à l'essai **et** à l'exécution. |
| Essai d'un script dans l'interface | Fait : écran **Scripts** (atelier) + méthode `scripts.test` ; bloc « Script JavaScript » dans l'éditeur de flux. |
| Revue de sécurité | Fait : [docs/security/revue-securite-phase-6.md](../security/revue-securite-phase-6.md) (revue interne, pas un audit externe). Note DSI mise à jour. |
| Python isolé (AppContainer + Job Object) | **Non fait.** |

## Défauts trouvés et corrigés pendant cette phase

1. **Source imbriquée** (`!!!!…1` ou `[[[…]]]` sur 64 Kio) : l'analyseur levait `InsufficientExecutionStackException`, non interceptée
   (erreur interne). Désormais refusée proprement (« imbrication trop profonde »). Le processus ne plantait pas.
2. **Sortie d'un bloc ignorée** : le pipeline ne reprenait pas l'enregistrement renvoyé par un bloc (les blocs existants modifiaient en place).
   Le script semblait sans effet dans un flux alors qu'il marchait isolément. Corrigé, test de chaînage script → contrôle → correspondance.

## Fichiers créés ou modifiés

- `src/ErpConnect.Flows/Scripts.cs` (nouveau) : `ScriptStep`, `ScriptLimits`, `ScriptException` ; conversion JSON ↔ `Record`.
- `src/ErpConnect.Flows/Mapper.cs` : type de bloc `script`, pipeline jetable (`IDisposable`), sortie d'un bloc reprise.
- `src/ErpConnect.Engine/FlowService.cs` : politique `ScriptsAllowed` ; `RunService` refuse d'exécuter un flux à script si désactivé ;
  `RunService`/`PreviewService` libèrent le pipeline.
- `src/ErpConnect.Core/Settings/EngineSettings.cs` : `ScriptsEnabled`.
- `src/ErpConnect.Contracts/` : `DtosPhase6.cs`, `scripts.test` (`flows.edit`), `scripts.limits` (`flows.read`), `SettingsDto.scriptsEnabled`,
  protocole 1.4 ; contrat TypeScript régénéré.
- `src/ErpConnect.Service/` : `RegisterScripts`, contrôle à l'aperçu, branchement de la politique.
- `ui/desktop/src/screens/Scripts.tsx` (nouveau), `Flows.tsx` (bloc script), `Settings.tsx` (case « Autoriser les scripts »), navigation.
- Tests : `ScriptTests.cs` (44, dont 29 de scripts malveillants et 2 dans le noyau), `ScriptRpcTests.cs` (4), `Scripts.test.tsx` (3), `Settings.test.tsx` (+1),
  matrice S3 étendue (2 méthodes), liste des méthodes.
- Docs : revue de sécurité, note DSI, ADR 0008 complétée, README.
- Paquet : `Jint` 4.18.0 (dépend d'`Acornima`), fichiers de verrouillage mis à jour.

## Ce qui fonctionne réellement (développé ET testé)

- Script dans un flux réel CSV → JSON : filtre (`null`), nouvelle forme d'enregistrement, compteurs exacts.
- Script bloqué (boucle infinie) dans un flux : exécution en **Échec**, rien de publié, message « Script arrêté ».
- Chaque limite arrête vraiment le script, en moins de 10 s (vérifié) ; les limites s'appliquent **par enregistrement**
  (50 enregistrements de 60 000 instructions passent sous un plafond de 100 000, un seul échoue sous un plafond de 10 000).
- Aucun objet système accessible (8 noms testés), pas de `GetType` sur l'enregistrement, prototype isolé entre deux blocs.
- Par le pipe : désactivé par défaut (`ENTITLEMENT_DENIED` à l'essai, champ `steps` en erreur à l'enregistrement), activation auditée,
  désactivation qui fait échouer l'exécution d'un flux déjà enregistré ; erreurs de syntaxe et d'exemple renvoyées par champ ;
  le moteur répond encore après un script bloqué.

## Tests réalisés et résultats (Linux, ce conteneur)

| Suite | Résultat |
|---|---|
| .NET, toutes suites (11 projets, performance comprise) | 427 réussis, 7 ignorés (Windows uniquement), 0 échec |
| Vitest | 42 réussis |
| ESLint, `tsc --noEmit` | propres |
| `dotnet restore --locked-mode` | OK |

## Ce qui n'est PAS testé

- Débit d'un flux avec script sur un gros volume (non mesuré ; un interpréteur est nettement plus lent que les expressions).
- Comportement sous Windows (même code géré ; les limites de pile peuvent différer, d'où l'interception ajoutée).
- Python isolé : non développé.

## Problèmes identifiés

- Le script s'exécute dans le processus du service : une faille de Jint aurait les droits (minimaux) du compte de service.
- La limite mémoire compte les allocations du thread, pas la mémoire vivante.
- `Date.now()` et `Math.random()` restent disponibles : un script peut ne pas être déterministe (une reprise peut donner un autre résultat).
- Le délai de 500 ms et les autres limites sont fixes (non configurables) ; un traitement légitime lourd peut être arrêté.
- Les variables globales d'un script persistent entre enregistrements d'une même exécution (documenté).

## Risques

- La sécurité du bac à sable dépend de Jint : suivre ses mises à jour de sécurité (la CI bloque les paquets vulnérables connus).
- Un concepteur peut écrire un script qui transforme mal des données : c'est un risque métier, couvert par l'aperçu, l'essai et la publication.

## Procédure de validation

1. Paramètres → cocher « Autoriser les scripts » (compte Administrateur), enregistrer ; vérifier l'entrée `settings.update` dans le journal d'audit.
2. Écran Scripts : essayer l'exemple, puis `while(true){}` (résultat « Arrêté »), puis `eval('1')` (« Rejeté »).
3. Dans un flux, ajouter un bloc « Script JavaScript », lancer l'aperçu, publier, exécuter ; vérifier le fichier produit.
4. Décocher « Autoriser les scripts » : relancer le flux, il doit échouer avec « scripts sont désactivés ».
5. Lire la [revue de sécurité](../security/revue-securite-phase-6.md) et la [note DSI](../security/guide-dsi.md).
