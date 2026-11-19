# ADR 0012 – Control Center déployable chez l'éditeur, le client ou un intégrateur

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Le Control Center est une application autonome, multi-organisations, installable chez l'éditeur, chez un client ou chez un intégrateur.

## Conséquences

Modèle de données multi-organisations dès la conception, isolation par Row-Level Security PostgreSQL. La clé de signature des licences reste chez l'éditeur.

## Alternatives écartées

SaaS unique éditeur : contraire au principe de non-dépendance au cloud.
