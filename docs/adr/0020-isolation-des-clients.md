# ADR 0020 – Isolation des clients : une base et une clé de chiffrement par client

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

En SaaS, la plateforme héberge les résultats de nombreux clients. Une erreur de filtrage ne doit pas suffire à exposer un client à un autre.

## Décision

- Une base PostgreSQL par client ; une clé de chiffrement par client pour les données sensibles et les sauvegardes.
- Le client courant est déterminé par le certificat de l'agent ou par le jeton de l'utilisateur (OIDC avec MFA), jamais par un paramètre.
- Sécurité au niveau des lignes (RLS) par société et groupe, en seconde barrière à l'intérieur de la base d'un client.
- Les modes cloud privé et on-premise utilisent le même mécanisme, avec un seul client.

## Conséquences

- Création d'un client = création d'une base, d'une clé et d'une autorité de certification, par une opération d'administration outillée.
- Tests systématiques : isolation des clients par chaque méthode de l'API, isolation des sociétés et des groupes, droits des utilisateurs (ADR 0023).
- Coût d'exploitation plus élevé qu'une base unique (migrations par base, supervision).

## Alternatives écartées

- Base unique avec colonne client et RLS seule : une erreur de requête ou de politique RLS exposerait tous les clients.
