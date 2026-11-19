# ADR 0004 – JSON-RPC 2.0 sur named pipe avec ACL

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

L'interface communique avec le service par JSON-RPC 2.0 sur le named pipe \\.\pipe\ERPConnect.Engine, protégé par une ACL.

## Conséquences

Aucun port réseau, aucune règle de pare-feu. Accès réseau au pipe explicitement refusé (SID NETWORK).

## Alternatives écartées

HTTP sur localhost : exposé à tous les utilisateurs de la machine et au DNS rebinding. gRPC : plus lourd à relayer depuis Rust.
