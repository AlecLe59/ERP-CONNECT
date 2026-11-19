# ERP Connect – Note de sécurité pour la DSI (première version, Phase 2)

> État : décrit le comportement **conçu et testé sous Linux pour la partie indépendante de Windows**. Les points
> marqués « à confirmer sous Windows » n'ont pas encore été vérifiés sur une machine Windows réelle.

## Réseau

- Aucun port TCP ou UDP ouvert (ADR 0010). L'interface parle au moteur par un named pipe local. *(S5, à confirmer sous Windows)*
- L'accès au pipe depuis le réseau est explicitement refusé (SID NETWORK). *(S2, à confirmer sous Windows)*

## Comptes et privilèges

- Le service `ERPConnectEngine` s'exécute sous le compte virtuel `NT SERVICE\ERPConnectEngine`, jamais LocalSystem.
- SID de service « restreint », privilèges limités à `SeChangeNotifyPrivilege` et `SeImpersonatePrivilege`
  (ce dernier sert uniquement à lire l'identité Windows du client du pipe). *(S6, à confirmer sous Windows)*
- Aucun mot de passe propre à ERP Connect : l'utilisateur est identifié par son jeton Windows (compatible AD).

## Droits d'accès

- Cinq groupes locaux : ERP Connect Administrateurs, Concepteurs, Opérateurs, Lecteurs, Auditeurs.
- L'ACL du pipe n'autorise que ces groupes et les Administrateurs de la machine. Un compte ou groupe AD doit être
  membre de l'un de ces groupes locaux pour atteindre le moteur, **et** avoir un rôle attribué dans ERP Connect.
- Tout ce qui n'est pas accordé est refusé ; chaque refus et chaque changement de droits est audité. Le dernier
  Administrateur ne peut pas être retiré.

## Données et secrets

- `%ProgramData%\ERPConnect` : héritage coupé, accès SYSTEM, Administrateurs et compte du service. `keys` :
  SYSTEM et compte du service uniquement. *(S7, à confirmer sous Windows)*
- Secrets chiffrés en AES-256-GCM (clé de 256 bits, nonce aléatoire, données associées liant chaque secret à
  sa ligne et à son usage). La clé maître est protégée par DPAPI dans le contexte du compte du service.
- Aucune méthode de l'interface ne renvoie un secret. Les journaux masquent les secrets connus et les motifs
  usuels (`password=`, `token:`…).
- **Limite assumée** : un administrateur de la machine peut, avec assez d'efforts, récupérer les secrets d'un
  service qui s'exécute sur cette machine. Le coffre protège contre la lecture des bases, des sauvegardes et des
  journaux, et contre les utilisateurs non administrateurs.
- Jusqu'à la Phase 8, une sauvegarde ne permet pas de restaurer les secrets sur une autre machine.

## Journal d'audit

Ajout seul, chaîné par SHA-256. Toute modification ou suppression d'une entrée est détectée par la vérification
d'intégrité (écran Journal d'audit, méthode `audit.verifyChain`).
