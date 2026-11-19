# ADR 0011 – Agent distant facultatif, HTTPS sortant uniquement

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Le pilotage distant est assuré par un service séparé, ERPConnect Agent, qui n'établit que des connexions HTTPS sortantes avec authentification mutuelle par certificat.

## Conséquences

Arrêter l'agent n'affecte pas les flux. L'agent n'a aucun accès aux secrets ni aux données métier.

## Alternatives écartées

Accès entrant au moteur : contraire à la politique de sécurité.
