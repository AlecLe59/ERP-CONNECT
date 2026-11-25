# ADR 0023 – Tests de sécurité systématiques et validation Windows avant production

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

La sécurité de l'architecture repose sur des propriétés qui ne se démontrent que par des tests ciblés, et sur un socle Windows
qui n'a jamais été installé sur une vraie machine.

## Décision

- **Tests de sécurité systématiques**, dans la porte de chaque phase concernée et rejoués à chaque modification : isolation des clients ;
  isolation des sociétés et des groupes ; droits des utilisateurs ; protection des certificats et des clés ; révocation des agents ;
  transferts non autorisés ; résistance aux coupures réseau ; protection contre les mises à jour malveillantes ou non signées.
- **Validation Windows préalable à toute mise en production** : service et compte, droits et ACL, coffre DPAPI, communications locales,
  installation MSI, mises à jour, restauration, sur une vraie machine Windows Server, avec rapport d'exécution.
- **Les tests sous Linux ne sont jamais présentés comme une validation complète de Windows.** La CI Windows exécute la suite .NET
  mais n'installe rien et ne vaut pas validation.
- Chaque rapport de phase distingue : code développé, tests unitaires, d'intégration, de sécurité, de performance, tests Windows,
  tests avec de véritables applications. Rien n'est déclaré « fait » sans démonstration.

## Conséquences

- Une machine Windows Server de test et des données réelles (ou réalistes) sont des prérequis à fournir.
- Une fonction dont les tests n'ont pu tourner que sous Linux est livrée avec la mention « non validé sous Windows ».

## Alternatives écartées

- Validation Windows en fin de projet seulement : les défauts du socle se découvriraient trop tard.
