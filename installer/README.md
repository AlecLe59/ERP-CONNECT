# Installateur (Phase 8) – ÉBAUCHE NON TESTÉE

Rien dans ce dossier n'a été construit ni exécuté : le développement s'est fait sous Linux, sans WiX ni Windows.
Les fichiers décrivent l'installation attendue ; ils doivent être validés sur Windows Server 2019/2022 et Windows 10/11.

| Fichier | Rôle |
|---|---|
| `wix/Package.wxs` | MSI du moteur : fichiers, service `ERPConnectEngine` (compte virtuel, SID restreint, 2 privilèges), dossiers de données avec ACL, source du journal des événements. |
| `scripts/New-ErpConnectGroups.ps1` | Création des 5 groupes locaux (à brancher en action différée). |

## Ce que l'installateur doit garantir (à vérifier après installation)

1. `sc qc ERPConnectEngine` : compte `NT SERVICE\ERPConnectEngine`, démarrage automatique différé.
2. `sc qsidtype ERPConnectEngine` : `RESTRICTED` ; `sc qprivs ERPConnectEngine` : `SeChangeNotifyPrivilege`, `SeImpersonatePrivilege`.
3. `icacls C:\ProgramData\ERPConnect\keys` : SYSTEM et `NT SERVICE\ERPConnectEngine` seulement, héritage coupé.
4. Observateur d'événements : source « ERP Connect » présente.
5. Désinstallation : le service est supprimé, `C:\ProgramData\ERPConnect` est **conservé** (bases, clés, sauvegardes).
6. Mise à jour : migration des bases avec copie préalable dans `backup\` (déjà faite par le moteur au démarrage).

## Points ouverts

- `util:PermissionEx` n'expose pas « couper l'héritage » de manière fiable selon les versions de WiX : prévoir une action
  `icacls /inheritance:r` si la vérification 3 échoue.
- La clé publique de licence est fixée à la publication (`-p:ErpConnectLicensePublicKey=…`) ; sans elle, l'installation reste en évaluation.
- L'interface Tauri a son propre MSI (bundler Tauri), non couvert ici.
- Signature Authenticode du MSI et des exécutables : non faite.
