# ADR 0021 – Mises à jour des agents signées, par un service séparé, avec retour arrière

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

Une mise à jour d'agent s'exécute chez le client avec des droits élevés : c'est le vecteur d'attaque le plus dangereux de l'architecture.

## Décision

- Les paquets sont signés par une clé de signature dédiée aux mises à jour, distincte de la clé des licences ; l'agent vérifie
  signature, empreinte et numéro de version avant toute installation.
- Un petit service de mise à jour séparé installe ; le service de l'agent reste sans droits d'administrateur.
- Le client choisit son canal (automatique, différé, manuel) ; aucune version antérieure n'est acceptée sauf retour arrière décidé localement.
- Retour arrière automatique si le service ne redémarre pas correctement.

## Conséquences

- Nécessite un certificat de signature de code et une procédure de garde de la clé (hors dépôt).
- Couvert par le test « mises à jour malveillantes ou non signées » (ADR 0023). Le passage réel sur Windows (MSI) reste à valider.

## Alternatives écartées

- Mise à jour par le service de l'agent lui-même : exigerait des droits d'administrateur permanents.
- Réutiliser la clé des licences : une fuite compromettrait à la fois les licences et le code exécuté chez les clients.
