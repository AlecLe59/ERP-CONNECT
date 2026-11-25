# ADR 0022 – Plateforme en ASP.NET Core (.NET 10) et PostgreSQL, sous réserve de validation

- Statut : Accepté sous réserve
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

La plateforme doit partager la bibliothèque de contrôle (ADR 0018) et tourner à l'identique en SaaS, en cloud privé et chez le client.

## Décision

- Application ASP.NET Core sur .NET 10, livrée en image de conteneur et en paquet Windows ; base PostgreSQL.
- **Réserve** : la compatibilité et le déploiement doivent être validés sur les environnements cibles (hébergeur SaaS retenu,
  cloud privé type, serveur Windows ou Linux d'un client on-premise, versions de PostgreSQL disponibles) au début de la phase 10,
  avant tout développement de la plateforme. Un échec remet cette décision en discussion.

## Conséquences

- Même langage que l'agent : bibliothèque partagée, outillage et tests communs.
- Un client on-premise doit accepter PostgreSQL ; c'est un point à vérifier dans la validation ci-dessus.

## Alternatives écartées

- SQL Server : licence à la charge du client on-premise, moins courant chez les hébergeurs cloud.
- Une autre pile pour la plateforme : bibliothèque de contrôle dupliquée.
