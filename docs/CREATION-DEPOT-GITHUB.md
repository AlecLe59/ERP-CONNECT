# Créer et rattacher le dépôt privé ERP-CONNECT

Ce dépôt n'existe pas encore : les fichiers ont été préparés, mais Claude n'a pas d'accès GitHub dans ce projet
et ne peut pas créer de dépôt. Voici les actions, dans l'ordre. `<OWNER>` = votre compte ou votre organisation GitHub.

## 1. Choisir le compte propriétaire et l'offre

- Recommandé : une **organisation** GitHub au nom de l'éditeur (transfert, équipe, droits plus simples).
- Les règles de protection de branches sur un dépôt **privé** exigent l'offre **GitHub Pro** (compte personnel)
  ou **GitHub Team** (organisation). Sans elles, les règles de l'étape 5 ne s'appliqueront pas.
- CodeQL et la revue des dépendances sur dépôt privé exigent l'option payante **GitHub Code Security** :
  les workflows sont prévus pour s'activer seulement si vous la prenez (variable `ENABLE_GHAS`).

## 2. Créer le dépôt vide

Sur github.com : **New repository** → nom `ERP-CONNECT` → **Private** → ne cochez **ni README, ni .gitignore, ni licence**.

Ou en ligne de commande :

```
gh repo create <OWNER>/ERP-CONNECT --private --description "ERP CONNECT – intégration universelle de données pour Windows"
```

## 3. Donner l'accès à Claude

1. Connectez votre compte GitHub à Claude : https://claude.ai/connect-github
2. Installez l'application GitHub **Claude** sur le dépôt `ERP-CONNECT` uniquement :
   https://github.com/apps/claude/installations/select_target
3. Ajoutez le dépôt au projet : **Paramètres du projet → Ressources**.
4. Prévenez Claude dans le fil : il poussera le commit initial avec ces fichiers.

Alternative sans accès Claude : décompressez `erp-connect-repo.zip` dans un dossier `ERP-CONNECT`, puis

```
cd ERP-CONNECT
git init -b main
git add .
git commit -m "chore: structure initiale du dépôt"
git remote add origin https://github.com/<OWNER>/ERP-CONNECT.git
git push -u origin main
```

## 4. Réglages du dépôt

```
gh repo edit <OWNER>/ERP-CONNECT \
  --enable-squash-merge --enable-merge-commit=false --enable-rebase-merge=false \
  --delete-branch-on-merge --enable-wiki=false
```

Dans **Settings → Code security** : activez *Private vulnerability reporting*, *Dependabot alerts* et
*Dependabot security updates*. Activez *Secret scanning / Push protection* si votre offre l'inclut.

Remplacez `@OWNER` dans `.github/CODEOWNERS` par votre identifiant GitHub.

## 5. Règles de branches

À appliquer **après** le premier push (la branche `main` doit exister) :

```
gh api repos/<OWNER>/ERP-CONNECT/rulesets -X POST --input .github/rulesets/main.json
```

Effet sur `main` et `release/**` : pas de suppression, pas de push forcé, historique linéaire,
Pull Request obligatoire, conversations résolues, et vérifications obligatoires
`Build .NET`, `Build UI`, `Build Tauri shell`, `Secrets scan`.

Le nombre d'approbations requises est à 0, car avec un seul développeur on ne peut pas approuver sa propre PR.
Passez-le à 1 dès qu'une seconde personne relit le code.

## 6. Secrets de la CI

Aucun secret n'est nécessaire avant la Phase 8 (signature de l'installateur). Le certificat de signature
ne sera **jamais** commité : il restera dans un service de signature (par ex. Azure Artifact Signing) ou dans
un environnement GitHub protégé, inaccessible aux PR.

## 7. Vérification

- Le dépôt apparaît comme **Private**.
- L'onglet **Actions** montre les workflows CI et Sécurité au vert sur `main` (étapes de build ignorées tant
  qu'il n'y a pas de code, c'est attendu).
- Un `git push` direct sur `main` est refusé.
