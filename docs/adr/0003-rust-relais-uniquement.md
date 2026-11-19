# ADR 0003 – Couche Rust limitée au relais

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

La couche Rust de Tauri ne contient aucune logique métier. Elle expose une seule commande, engine_call, et relaie les messages JSON-RPC vers le named pipe.

## Conséquences

Code Rust réduit (quelques centaines de lignes), testé par cargo test et clippy. Aucun plugin Tauri d'accès au système de fichiers ou au shell.

## Alternatives écartées

Logique dans Rust : dupliquerait le moteur dans un second langage.
