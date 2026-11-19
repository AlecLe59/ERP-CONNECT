# ADR 0001 – Moteur d'intégration en .NET 10 LTS

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

Le moteur, le service et les connecteurs sont développés en C# sur .NET 10 LTS.

## Conséquences

Écosystème complet pour SQL Server, Oracle, SOAP, Microsoft 365 et l'intégration Windows. Support jusqu'en novembre 2028 ; migration LTS à planifier. Empreinte mémoire supérieure à Rust (environ 100 Mo au repos).

## Alternatives écartées

Rust : performances et empreinte meilleures, mais connecteurs Oracle, SOAP et Microsoft 365 immatures ; surcoût estimé à 20-35 % sur les connecteurs.
