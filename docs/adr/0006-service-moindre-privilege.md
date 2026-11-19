# ADR 0006 – Service Windows sous compte à privilèges minimaux

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Le service s'exécute sous le compte virtuel NT SERVICE\ERPConnectEngine (service ERPConnectEngine), jamais LocalSystem. Un gMSA peut être configuré pour les accès réseau authentifiés.

## Conséquences

Accès limité à %ProgramData%\ERPConnect et aux ressources accordées explicitement.

## Alternatives écartées

LocalSystem : privilèges excessifs.
