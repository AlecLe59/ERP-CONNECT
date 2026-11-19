# ADR 0008 – Scripts : JavaScript (Jint) avant Python isolé

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Le mode EXPERT propose d'abord JavaScript via Jint, avec limites de temps, mémoire et récursion et sans accès .NET. Python viendra ensuite dans un processus séparé (AppContainer + Job Object).

## Conséquences

Scripts désactivés par défaut. Python après la Phase 6.

## Alternatives écartées

Python en processus : aucun bac à sable fiable.
