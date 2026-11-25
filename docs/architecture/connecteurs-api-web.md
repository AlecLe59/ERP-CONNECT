# Connecteurs d'API web : REST et SOAP

## REST (`rest`)

### Authentification

| Mode | Champs | Remarque |
| --- | --- | --- |
| `none` | | |
| `basic` | utilisateur, mot de passe | |
| `bearer` | jeton | Jeton fixe fourni par l'éditeur de l'API |
| `apiKey` | nom de l'en-tête, clé | |
| `oauth2` | URL du jeton, identifiant client, secret client, portée | Flux « client credentials ». Jeton demandé une fois, gardé en mémoire jusqu'à une minute avant son expiration, renouvelé une seule fois si l'API répond 401. Identifiants envoyés dans le corps (`post`, par défaut) ou en en-tête (`basic`), selon le fournisseur. |

Les secrets (mot de passe, jeton, clé, secret client) sont stockés dans le coffre chiffré. L'URL du jeton doit être en HTTPS ;
c'est le seul hôte, en plus de celui de l'API, vers lequel le connecteur envoie une requête.

**En-têtes supplémentaires** : une paire `Nom: valeur` par ligne, pour les valeurs non secrètes (identifiant de société, langue).
`Authorization` et `Cookie` y sont refusés : ce sont des secrets, à mettre dans les champs d'authentification.

### Pagination (options de la source)

| `pagination` | Options | Fin de lecture |
| --- | --- | --- |
| `nextLink` (défaut si `nextLinkPath` est donné) | `nextLinkPath` : chemin du lien suivant dans la réponse (`@odata.nextLink`, `links.next`) | Lien absent |
| `linkHeader` | | En-tête `Link` sans `rel="next"` (GitHub, Shopify…) |
| `page` | `pageParam` (`page`), `pageStart` (1), `pageSizeParam`, `pageSize` | Page vide, ou page incomplète si la taille est connue |
| `offset` | `offsetParam` (`offset`), `limitParam` (`limit`), `pageSize` (100) | Page incomplète |
| `cursor` | `cursorPath` : chemin du curseur dans la réponse, `cursorParam` (`cursor`) | Curseur absent ou vide |

`itemsPath` donne le chemin du tableau d'éléments dans la réponse (`data`, `value`, `results`). L'adresse de la page suivante
sert de point de reprise : après une coupure, la lecture reprend à la page où elle s'était arrêtée. Un lien vers un autre hôte
est refusé, pour qu'une réponse piégée ne puisse pas récupérer le jeton.

## SOAP (`soap`)

Pour les services web plus anciens (ERP, administrations, banques), en SOAP 1.1 ou 1.2.

- **Adresse du service** : l'URL complète du point d'accès (par exemple `https://erp.example/ws/Service.asmx`). Dans un flux,
  la ressource `/` désigne cette adresse telle quelle.
- **Authentification** : basique, jeton, OAuth2 (comme REST) ou **WS-Security** (`wsse` : utilisateur et mot de passe dans
  l'en-tête de l'enveloppe, valeurs échappées).
- **Lecture** : options `soapAction`, `body` (le contenu de `<soap:Body>`, en XML) et `recordElement` (le nom de l'élément répété
  dans la réponse, par exemple `Client`). Chaque élément devient un enregistrement : attributs en `@nom`, sous-éléments en champs.
  Un appel, sans pagination ; en cas de coupure, l'appel est refait en entier.
- **Écriture** : options `soapAction` et `bodyTemplate`, par exemple
  `<CreerClient xmlns="urn:erp"><Code>{code}</Code><Nom>{nom}</Nom></CreerClient>`. Un appel par enregistrement ; chaque `{champ}`
  est remplacé par la valeur échappée pour XML (une valeur ne peut pas injecter d'élément). Un champ absent rejette la ligne seule.
- **Erreurs** : une faute SOAP « Client » ou « Sender » est permanente (la ligne est rejetée avec le message du service),
  « Server » ou « Receiver » est transitoire (nouvelles tentatives). Les réponses sont lues sans DTD ni entité externe.

## Ce qui a été testé, et où

| Domaine | Testé | Comment |
| --- | --- | --- |
| OAuth2, pagination, en-têtes | Oui, sous Linux | Serveur HTTP local réel (pile réseau du système), 15 tests ; flux complet API OAuth2 paginée → CSV par le moteur |
| SOAP 1.1 et 1.2, WS-Security, fautes, modèle d'écriture | Oui, sous Linux | Serveur HTTP local qui vérifie l'enveloppe reçue, 8 tests ; flux complet CSV → SOAP avec une ligne refusée par le service |
| Fournisseurs réels | **Non** | Aucune API du marché (Microsoft, Salesforce, Sage, Pennylane…) ni aucune pile SOAP du marché (WCF, Axis, SAP) n'a été appelée |
| HTTPS réel | **Non** | Les tests passent en HTTP local ; la validation des certificats est celle de .NET, non testée ici |
| Windows | **Non** | |
