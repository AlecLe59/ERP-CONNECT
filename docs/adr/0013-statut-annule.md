# ADR 0013 – Septième statut d'exécution « Annulé »

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Les exécutions ont sept statuts : En attente, En cours, Réussi, Réussi avec avertissements, Échec, Suspendu, Annulé.

## Conséquences

Annulé est terminal. Il ne défait pas les écritures déjà confirmées ; le rapport les liste.

## Alternatives écartées

Annulation traitée comme Échec : confondrait décision humaine et erreur.
