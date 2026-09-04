# Contributing to Bastion Vault

Thank you for considering a contribution. Bastion Vault is a security tool, so the bar for
changes is deliberately high; this page explains how to clear it.

## License of contributions

Bastion Vault is licensed under the **PolyForm Noncommercial License 1.0.0** (see `LICENSE`).
By submitting a contribution you agree that:

- your contribution is licensed to the project under the same PolyForm Noncommercial 1.0.0
  terms ("inbound = outbound"), and
- you have the right to do so (it is your own work, or you are allowed to contribute it
  under these terms), and
- the maintainer may additionally offer the project, including your contribution, under a
  commercial license to third parties.

Every commit must carry a **Developer Certificate of Origin** sign-off, which certifies
the points above (https://developercertificate.org/):

```
git commit -s
```

adds the `Signed-off-by: Your Name <you@example.com>` trailer. Pull requests with unsigned
commits are not merged.

## Commit messages: Conventional Commits

All commits follow the [Conventional Commits](https://www.conventionalcommits.org/)
convention. A handy cheatsheet: https://gist.github.com/qoomon/5dfcdf8eec66a051ecd85625518cfd13

```
<type>(<optional scope>): <short summary in imperative mood>

<optional body: what and why, not how>

<optional footers: BREAKING CHANGE:, Fixes #123, Signed-off-by:>
```

Types used here: `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `build`, `ci`, `chore`,
`style`. Scopes in this repository: `core`, `format`, `crypto`, `app`, `theme`, `docs`,
`tests`, `ci`. Examples:

```
fix(core): report SessionLocked instead of SaveVerificationFailed on concurrent lock
feat(app): add overflow menu to the command bar
docs(format): pin the index padding ladder
```

Anything that changes the on-disk format or the public Core API is a `BREAKING CHANGE`
and needs a format version discussion first (see below).

## Before you start

- Read `docs/FORMAT.md` (normative format), `docs/API.md` (frozen Core surface),
  `docs/THREAT-MODEL.md` and `docs/UI-CONTRACT.md` (design language and UI rules).
- Read `docs/DEVELOPING.md` for the build, the demo mode, the screenshot workflow and
  the ownership map.
- Open an issue before large changes so the approach can be agreed on.

## Rules that are not negotiable

1. **No format change without a spec change.** `docs/FORMAT.md` is updated first; the
   golden fixtures in `tests/fixtures` must still compare byte-for-byte unless the format
   version is bumped, and then they are regenerated deliberately (`BASTION_REGEN_GOLDEN=1`).
2. **No new cryptographic primitives or parameters** without an issue explaining the
   threat they address and the test vectors that prove the implementation.
3. **Secrets never become `string`.** Passwords and keys stay in the pinned, zeroed
   buffers described in `docs/API.md`.
4. **No plaintext on disk** except through explicit export.
5. **View models never reference WPF types**; every OS touchpoint sits behind an interface
   in `src/BastionVault.App/Services`.
6. **Warnings are errors.** CI builds with `-warnaserror`.
7. **Tests accompany changes.** A bug fix adds the regression test that would have caught
   it; a feature adds tests at the layer it lives in.

## Building and testing

```
dotnet build BastionVault.slnx -warnaserror
dotnet test  BastionVault.slnx
```

Requires the .NET 10 SDK on Windows 10/11 (x64). The App test project runs headless.

## Pull request checklist

- [ ] Conventional Commits with DCO sign-off on every commit
- [ ] `dotnet build -warnaserror` and `dotnet test` pass locally
- [ ] Spec/docs updated when behaviour, format or API changed
- [ ] New or changed UI verified visually (screenshots in the PR help)
- [ ] No entry names, in-vault paths, keys or salts added to logs or error messages
- [ ] `CHANGELOG.md` entry under *Unreleased*

## Reporting security problems

Not through issues or pull requests. See `SECURITY.md`.
