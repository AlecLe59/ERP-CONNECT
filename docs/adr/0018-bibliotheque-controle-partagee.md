# ADR 0018 – Bibliothèque de contrôle partagée entre l'agent et la plateforme

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

Les mêmes règles doivent pouvoir s'exécuter sur le site du client (données locales) et sur la plateforme (données de plusieurs
sites, au niveau 5). Deux moteurs divergeraient.

## Décision

- Une bibliothèque .NET unique, sans dépendance au service, à Windows ni au réseau, contient : le modèle canonique, le langage de règles,
  les types de contrôle (ligne, agrégat, référence, doublon, rapprochement entre N sources, habilitation), la production des anomalies
  avec leurs preuves et leur degré de certitude.
- L'agent et la plateforme référencent la même version ; chaque résultat porte la version de la bibliothèque et des règles qui l'ont produit.
- Une anomalie n'énonce une cause que si les données disponibles la démontrent ; sinon elle est « potentielle » et liste ce qui manque.

## Conséquences

- La bibliothèque est développée en phase 9 et testée seule (tests unitaires, jeux de données de référence) avant d'être branchée sur l'agent.
- Ses évolutions suivent un versionnement strict ; un format de règle reste lisible sur deux versions antérieures.

## Alternatives écartées

- Contrôles seulement sur la plateforme : impose le transfert des données.
- Deux implémentations : résultats différents selon le lieu d'exécution.
