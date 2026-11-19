# ADR 0007 – Coffre de secrets : clé maître DPAPI + AES-256-GCM

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Une clé maître de 256 bits, protégée par DPAPI, chiffre chaque secret en AES-256-GCM. Les secrets ne sont jamais renvoyés à l'interface.

## Conséquences

La restauration sur une autre machine passe par un export chiffré par phrase de passe (Argon2id).

## Alternatives écartées

Gestionnaire d'identifiants Windows seul : lié à un profil interactif, limité en taille.
