# ADR 0005 – Authentification locale par identité Windows

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

L'utilisateur est identifié par le jeton Windows du client du named pipe (SID et groupes). ERP Connect ne stocke aucun mot de passe local.

## Conséquences

Compatible Active Directory sans configuration. Les rôles sont attribués à des comptes ou groupes Windows.

## Alternatives écartées

Comptes propres à ERP Connect : mots de passe supplémentaires à protéger et à gérer.
