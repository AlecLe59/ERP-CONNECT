# ADR 0008 – Scripts : JavaScript (Jint) avant Python isolé

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Le mode EXPERT propose d'abord JavaScript via Jint, avec limites de temps, mémoire et récursion et sans accès .NET. Python viendra ensuite dans un processus séparé (AppContainer + Job Object).

## Conséquences

Scripts désactivés par défaut. Python après la Phase 6.

## Mise en œuvre (Phase 6)

Jint 4.18, bloc « script » (`function transform(record)`), interop désactivée, `eval` et modules interdits, limites
par enregistrement (voir `ScriptLimits`), paramètre `ScriptsEnabled` (faux par défaut). Python isolé : non développé.

## Alternatives écartées

Python en processus : aucun bac à sable fiable.
