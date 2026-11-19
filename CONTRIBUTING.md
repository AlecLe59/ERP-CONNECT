# Contribuer à ERP CONNECT

## Règles fondamentales

1. Ne jamais casser une fonctionnalité déjà validée : toute PR doit laisser la CI au vert.
2. Ne jamais présenter une fonctionnalité comme opérationnelle si elle n'est pas développée et testée.
3. Chaque connecteur possède ses tests de conformité, unitaires et d'intégration.
4. Moteur, interface et connecteurs restent séparés : aucune dépendance de `src/` vers `ui/`.
5. Aucune limite commerciale codée en dur : elles passent par le service de droits.
6. Aucun secret dans le dépôt (mots de passe, jetons, clés, certificats `.pfx`/`.p12`/`.key`, fichiers `.env`).
7. Toute décision d'architecture nouvelle ou modifiée fait l'objet d'un ADR dans `docs/adr/`.

## Branches

| Branche | Usage |
| --- | --- |
| `main` | Toujours livrable. Protégée : aucun push direct, aucune réécriture d'historique. |
| `feature/<sujet>` | Nouvelle fonctionnalité, par ex. `feature/rpc-named-pipe` |
| `fix/<sujet>` | Correction |
| `docs/<sujet>` | Documentation seule |
| `security/<sujet>` | Correctif de sécurité (peut rester privé avant publication) |
| `release/<x.y>` | Stabilisation d'une version |

Les branches partent de `main` et y reviennent par Pull Request, fusionnée en *squash*.

## Messages de commit

Format [Conventional Commits](https://www.conventionalcommits.org/fr/) :

```
<type>(<portée>): <résumé à l'impératif>
```

Types : `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`, `security`.
Portées usuelles : `engine`, `service`, `rpc`, `storage`, `security`, `ui`, `installer`, `connector-<nom>`.

Exemple : `feat(rpc): authentifier le client du named pipe par son SID Windows`

## Pull Requests

- Une PR = un sujet. Remplir intégralement le modèle de PR.
- La CI (build .NET, build UI, coquille Tauri, scan de secrets) doit être verte.
- Toutes les conversations de revue doivent être résolues avant fusion.
- Indiquer honnêtement ce qui est testé, ce qui ne l'est pas, et les risques.

## Tests

- .NET : xUnit. Tests unitaires dans `tests/<Projet>.Tests`, intégration dans `tests/*.IntegrationTests`.
- Interface : Vitest + Testing Library.
- Rust : `cargo test`, `cargo clippy -- -D warnings`.
- Les tests de sécurité (`tests/ErpConnect.SecurityTests`) ne doivent jamais être désactivés pour obtenir une CI verte.

## Versions

SemVer (`MAJEUR.MINEUR.CORRECTIF`). Tags `vX.Y.Z` sur `main`. Le protocole JSON-RPC a sa propre version.
