# ADR 0019 – Identité des agents par certificat individuel, renouvelable et révocable

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

La plateforme doit savoir avec certitude quel agent, de quel client, lui parle ; l'identité du client ne doit jamais venir d'un paramètre de requête.

## Décision

- Enrôlement par jeton à usage unique et de courte durée, créé par un administrateur du client. L'agent génère sa propre paire de clés
  (non exportable, protégée par le compte du service) ; seule la demande de certificat sort.
- Une autorité de certification dédiée par client signe un certificat par agent, de durée limitée, renouvelé automatiquement avant échéance.
- Authentification mutuelle TLS à chaque connexion ; le client et l'agent sont déduits du certificat.
- Révocation immédiate par la plateforme (liste consultée à chaque connexion) ; un agent révoqué est refusé et ses lots en attente rejetés.
- Un agent appartient à un seul client pour toute sa vie ; changer de client exige un nouvel enrôlement.

## Conséquences

- Développé en phase 10. Couvert par les tests « protection des certificats et des clés » et « révocation » (ADR 0023).
- Le stockage de la clé privée dépend de Windows (magasin de certificats ou coffre DPAPI) : à valider sur une vraie machine.

## Alternatives écartées

- Clé d'API partagée : impossible de révoquer un agent seul, secret copiable.
- Certificat commun à tous les agents d'un client : la compromission d'un poste compromet tout le client.
