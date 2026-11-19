# ADR 0009 – Installateur MSI avec WiX

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

L'installation est livrée en MSI construit avec WiX Toolset.

## Conséquences

Installation et configuration natives du service, des ACL et des groupes ; déploiement silencieux GPO/SCCM/Intune.

## Alternatives écartées

MSIX : support limité des services Windows et des serveurs.
