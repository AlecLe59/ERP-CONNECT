# ADR 0016 – Agent de contrôle et plateforme centrale, trois modes d'hébergement, un seul code

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)
- Remplace : ADR 0011, ADR 0012

## Contexte

ERP-CONNECT évolue d'un moteur d'intégration vers un contrôleur des systèmes d'information. Les clients veulent garder la
maîtrise de leurs données ; certains acceptent un SaaS, d'autres exigent un cloud privé ou une installation sur leurs serveurs.
L'ADR 0011 décrivait un agent sans accès aux données métier, et l'ADR 0012 un Control Center sans offre SaaS : les deux sont dépassés.

## Décision

- **Le contrôle va aux données.** L'agent installé chez le client (le service Windows actuel) collecte, contrôle, conserve
  les preuves et fonctionne sans Internet ni plateforme. La plateforme centrale consolide ce que la politique de transfert
  (ADR 0017) l'autorise à recevoir.
- **Trois modes, un seul logiciel** : SaaS mutualisé, cloud privé, on-premise. Même code, même image de plateforme, même agent ;
  les différences sont des paramètres de déploiement, jamais des branches de code.
- **Ordre de construction** : phase 9 = contrôle local dans l'agent ; phase 10 = plateforme centrale et canal sécurisé.
  La phase 9 démarre après validation explicite de son périmètre.
- Les échanges partent toujours de l'agent, en HTTPS sortant avec certificat client (ADR 0019) ; aucun port entrant (ADR 0010).

## Conséquences

- Tout l'existant des phases 2 à 8 devient l'agent ; aucune refonte avant la phase 9.
- Un protocole agent ↔ plateforme versionné est à concevoir (boîte d'envoi durable, lots idempotents, accusés de réception,
  compatibilité sur deux versions antérieures).
- Chaque version doit être testée dans les trois modes ; c'est l'essentiel du surcoût par rapport à un SaaS seul.

## Alternatives écartées

- Plateforme seule, l'agent se contentant d'extraire : les données brutes quitteraient le client, et rien ne fonctionnerait hors ligne.
- Code distinct par mode : maintenance multipliée et écarts de sécurité entre modes.
