# Langage d'expressions (Phase 4)

Les expressions servent dans la correspondance (une expression par champ cible) et dans les blocs de transformation
(`filter`, `set`, `validate`). Elles sont compilées une fois par exécution puis évaluées pour chaque enregistrement.

## Garanties de sûreté

- Aucune boucle, aucune variable, aucun accès au système, au réseau, aux fichiers ou à la réflexion : seules les fonctions
  de la liste ci-dessous existent. Le temps d'évaluation est donc proportionnel à la taille de l'expression.
- Expression limitée à 4 096 caractères et 64 niveaux d'imbrication ; tout texte produit est limité à 1 Mio.
- Expressions régulières : délai de 100 ms par appel (protection contre les motifs catastrophiques).
- Une erreur à l'évaluation (conversion impossible, division par zéro, `error('…')`, dépassement décimal) ne plante pas
  l'exécution : l'enregistrement est **rejeté** avec le motif, conservé dans les rejets de l'exécution.

## Syntaxe

| Élément | Exemple |
|---|---|
| Champ | `[nom]` (nom littéral, y compris avec points ou espaces : `[code.article]`), `client.ville`, `source.client.ville`, `lignes.0.qte` |
| Texte | `'Bonjour'`, apostrophe doublée : `'l''été'` |
| Nombre | `12`, `3.5` (point décimal ; entiers 64 bits, décimaux exacts — pas de flottants binaires) |
| Constantes | `true`, `false`, `null` |
| Opérateurs | `+ - * / %`, concaténation `&`, comparaison `= <> < <= > >=`, logique `and or not` |

Priorité (de la plus faible à la plus forte) : `or`, `and`, `not`, comparaisons, `&`, `+ -`, `* / %`.
Un dépassement d'entier bascule en décimal exact ; un dépassement décimal est une erreur.
`null` se propage dans les opérations arithmétiques et les fonctions de texte (`upper(null)` = `null`) ; `concat` le traite comme vide.

## Fonctions

Texte : `upper`, `lower`, `trim`, `len`, `left`, `right`, `substr` (1 = premier caractère), `replace`, `contains`,
`startsWith`, `endsWith`, `padLeft`, `padRight`, `concat`, `split(texte, séparateur, rang)`, `regexMatch`, `regexReplace`.

Valeurs vides et conditions : `coalesce`, `isNull`, `isEmpty`, `if(condition, siVrai, siFaux)` (paresseux),
`switch(valeur, cas1, résultat1, …, défaut?)`, `error(message)`.

Nombres : `toNumber(texte, séparateurDécimal?)` (`toNumber('1.234,56', ',')`), `toInteger`, `round` (arrondi commercial),
`floor`, `ceil`, `abs`, `min`, `max`.

Conversions et dates : `toText`, `toBool` (`true/1/oui`), `toDate(texte, format?)`, `formatDate(date, format)`, `addDays`, `now`, `today`.

La liste exacte avec signatures est renvoyée par la méthode RPC `flows.functions` et affichée dans l'éditeur.

## Blocs de transformation

Appliqués dans l'ordre sur une copie de l'enregistrement source, avant la correspondance :

| Bloc | Paramètres | Effet |
|---|---|---|
| `filter` | `expression` | garde l'enregistrement si vrai ; sinon il est **filtré** (compté dans « filtrés », pas un rejet) |
| `set` | `field`, `expression` | crée ou remplace un champ |
| `rename` | `field`, `target` | renomme un champ |
| `remove` | `field` (liste séparée par des virgules) | supprime des champs |
| `validate` | `expression`, `message` | rejette l'enregistrement avec `message` si faux |

100 blocs au maximum par flux.
