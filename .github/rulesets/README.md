# Ruleset templates

GitHub does not read rulesets from the repository; these files are the record of what is
configured and can be imported with *Settings → Rules → Rulesets → New ruleset → Import a
ruleset*. Re-import after editing a template.

| File | Target | Effect |
|---|---|---|
| `main.json` | branch `main` | pull request required (0 reviews, threads resolved), CI check required and up to date, linear history, **merge only** so a release is a fast-forward of `dev`, no force pushes, no deletion |
| `dev.json` | branch `dev` | the same, with **squash and rebase** merges for feature pull requests |
| `release-tags.json` | tags `v*` | tags cannot be created over an existing one, moved or deleted |

All three grant the *Repository admin* role an "always" bypass, so the maintainer can push a
hotfix or fix a mistaken tag; GitHub shows a warning and asks for confirmation each time.
Remove the `bypass_actors` entry to bind the maintainer as well.

`integration_id` 15368 is GitHub Actions; the status-check `context` must equal the job name
in `.github/workflows/ci.yml` exactly.
