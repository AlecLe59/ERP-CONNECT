# ADR 0015 – Vérification du serveur par le propriétaire de l'objet pipe

- Statut : Proposé (à valider par le responsable du projet)
- Date : 2026-11-19
- Décideur : responsable du projet

## Contexte

Le plan prévoyait que le relais vérifie que le processus serveur s'exécute sous `NT SERVICE\ERPConnectEngine`.
Ouvrir le jeton d'un processus de service depuis un compte standard est généralement refusé par Windows.

## Décision

Le relais Rust lit le **propriétaire** de l'objet pipe (`GetSecurityInfo`) et le compare au SID de
`NT SERVICE\ERPConnectEngine`. Le service se déclare propriétaire à la création du pipe. Un faux serveur ne peut
pas s'attribuer ce propriétaire sans le privilège de restauration. De plus, le service crée la première instance
avec `FILE_FLAG_FIRST_PIPE_INSTANCE` et refuse de démarrer si le nom est déjà pris (S4).

## Conséquences

Contrôle possible depuis un compte standard. Un administrateur de la machine peut toujours contourner ce
contrôle, ce qui est cohérent avec la limite assumée du coffre de secrets. À confirmer par S4 sur Windows.

## Alternatives écartées

Contrôle du jeton du processus serveur : accès refusé pour les utilisateurs standard.
