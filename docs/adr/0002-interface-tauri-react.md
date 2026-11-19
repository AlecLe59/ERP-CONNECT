# ADR 0002 – Interface Tauri 2 + React + TypeScript

- Statut : Accepté
- Date : 2026-11-19
- Décideur : responsable du projet (validation de l'étude d'architecture, Phase 1)

## Décision

L'application desktop est une application Tauri 2 dont l'interface est écrite en React + TypeScript.

## Conséquences

WebView2 doit être embarqué dans l'installateur (absent par défaut sur Windows Server). Composants React réutilisables par le Control Center.

## Alternatives écartées

Hôte .NET (WPF + WebView2) : supprimerait Rust, mais modèle de permissions moins strict.
