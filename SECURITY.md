# Politique de sécurité

## Signaler une vulnérabilité

Ne créez pas d'issue publique. Utilisez la fonction « Report a vulnerability » de l'onglet Security
du dépôt, ou contactez directement le responsable du projet.

## Règles du dépôt

- Aucun secret, mot de passe, jeton, clé privée, certificat de signature (`.pfx`, `.p12`, `.key`, `.pem` privé)
  ni fichier de licence signé ne doit être commité.
- Les certificats de signature de code sont stockés hors du dépôt (service de signature ou secrets chiffrés
  de la CI) et ne sont jamais accessibles aux PR provenant de forks.
- Chaque PR est analysée par un scan de secrets (gitleaks). Un secret détecté bloque la fusion ;
  un secret déjà poussé est considéré comme compromis et doit être révoqué, pas seulement supprimé.

## Principes produit

Voir `docs/security/` : aucun port entrant par défaut, secrets chiffrés (DPAPI + AES-256-GCM),
moindre privilège, audit chaîné, communications TLS vérifiées.
