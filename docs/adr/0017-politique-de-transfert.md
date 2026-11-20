# ADR 0017 – Politique de transfert des données, signée sur l'agent

- Statut : Accepté
- Date : 2026-11-20
- Décideur : responsable du projet (validation de l'architecture du 20/11/2026, avec réserves)

## Contexte

Le client doit décider, source par source et domaine par domaine, de ce qui quitte son réseau, et pouvoir le vérifier.
L'exploitant d'un SaaS ne doit pas pouvoir élargir ce périmètre à distance.

## Décision

- Six niveaux : 0 Local, 1 Indicateurs, 2 Anomalies sans données, 3 Preuves pseudonymisées, 4 Preuves en clair, 5 Données canoniques.
- **Le niveau 0 est le réglage initial** de tout agent, dans tous les modes ; aucune option d'installation, modèle ou licence ne le change.
- **Supervision technique séparée des données métier** : message distinct, liste fermée et versionnée de champs (identifiant de
  l'agent, versions, dates de contact et d'exécution, état du service, état des sources par identifiant opaque et code normalisé,
  lots en attente, espace disque, expirations, niveau en vigueur et empreinte de la politique). Jamais de noms, montants, chemins,
  requêtes ni messages d'erreur d'origine.
- **Chaque niveau est présenté en langage simple** dans la console locale, avec un exemple réel et un aperçu du prochain envoi avant signature.
- **Le niveau 5 s'accorde champ canonique par champ canonique**, pour une règle et une finalité nommées ; tout champ non listé est refusé.
- **Seul un administrateur autorisé du client** modifie la politique, depuis la console locale. Chaque version est signée par une clé
  propre au client conservée dans le coffre de l'agent, inscrite dans le journal d'audit chaîné et vérifiée à chaque démarrage et
  avant chaque envoi. Signature invalide : comportement du niveau 0 et alerte locale.
- **La plateforme ne modifie jamais les autorisations à distance** : le protocole n'a aucun message pour cela, l'agent rejette toute
  politique reçue par le réseau. La plateforme peut afficher une proposition, sans effet.
- Double contrôle : filtre de sortie sur l'agent, refus à la réception par la plateforme de tout ce qui dépasse la politique déclarée.
- **Empreintes du niveau 3** : HMAC-SHA-256 avec une clé secrète propre au client, générée chez lui, partagée entre ses agents par
  export chiffré par phrase de passe, jamais transmise à la plateforme.
- Tout transfert est journalisé des deux côtés (date, niveau, volume, empreinte du contenu).

## Conséquences

- Le filtre de sortie est un composant de sécurité à part entière, couvert par les tests « transferts non autorisés » (ADR 0023).
- Aux niveaux 2 et 3, la consultation des preuves se fait sur la console de l'agent.
- La phase 9 implémente la politique au niveau 0 seulement (rien ne sort) ; le filtre et la signature des niveaux 1 à 5 viennent en phase 10.

## Alternatives écartées

- Politique réglée depuis la plateforme : permettrait une extraction à distance par l'exploitant.
- Empreintes sans clé (SHA-256 simple) : un IBAN ou un SIREN se retrouve par essais successifs.
- Autorisation globale au niveau 5 : contraire à la minimisation des données.
