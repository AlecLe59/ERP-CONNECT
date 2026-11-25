# ADR 0014 – Routage JSON-RPC écrit dans le projet plutôt que StreamJsonRpc

- Statut : Proposé (à valider par le responsable du projet)
- Date : 2026-11-19
- Décideur : responsable du projet

## Contexte

Le plan de Phase 2 retenait StreamJsonRpc. Les exigences de sécurité du protocole (refus d'un message de plus de
4 Mo avant lecture, profondeur JSON limitée à 64, `session.hello` obligatoire, permission vérifiée à chaque appel
et refus audité, erreurs sans détail interne) demandent un contrôle fin de chaque étape.

## Décision

Le découpage des messages (`MessageFraming`) et le routage (`RpcDispatcher`) sont écrits dans `ErpConnect.Rpc`
avec `System.Text.Json`, environ 400 lignes testées. Les lots JSON-RPC (batch) sont refusés.

## Conséquences

Une dépendance de moins, chaque contrôle est visible et testé (tests Rpc et S12). En contrepartie, le code de
protocole est à maintenir par l'équipe.

## Alternatives écartées

StreamJsonRpc : plus complet (annulation, flux), mais les contrôles de taille, de profondeur et d'autorisation
auraient dû être greffés autour de lui.
