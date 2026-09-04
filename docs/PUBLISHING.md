# Publishing checklist

What has been done in the repository, what still has to be done in the GitHub UI, and
the reasoning behind the licensing and legal choices. Written for the maintainer.

## 1. Done in the repository

- `LICENSE` is the PolyForm Noncommercial License 1.0.0 (text from the SPDX license list).
  `NOTICE` carries the "Required Notice" line the license asks licensees to pass on.
- `THIRD-PARTY-NOTICES.md` reproduces the MIT licenses of the two redistributed libraries.
- `SECURITY.md`, `CONTRIBUTING.md` (inbound = outbound, DCO sign-off, Conventional
  Commits), `CODE_OF_CONDUCT.md`, `CHANGELOG.md`.
- `.github/`: CI on `windows-latest` (Release build with `-warnaserror`, full test run,
  draft release with zipped binaries and SHA-256 sums on `v*` tags), Dependabot for NuGet
  and Actions, issue forms, pull request template, `CODEOWNERS`.
- The command-line test hooks are compiled into Debug builds only.
- Assembly metadata carries author, copyright, license expression and repository URL.

## 2. To do in the GitHub UI (needs the owner's login)

1. **Settings → General**: description "Encrypted archive editor for Windows with its own
   vault format (source-available, non-commercial)"; topics `encryption`, `archive`,
   `wpf`, `dotnet`, `csharp`, `argon2`, `aes-gcm`, `windows`, `security`; disable Wiki;
   Discussions optional.
2. **Settings → Code security**: enable *Private vulnerability reporting*, *Dependabot
   alerts* and *Dependabot security updates*. `SECURITY.md` and the issue chooser link to
   the private advisory form.
3. **Settings → Rules → Rulesets**: import the three templates from `.github/rulesets/`
   (*New ruleset → Import a ruleset*): `main.json` (pull request required, CI check
   `Build and test (windows, .NET 10)`, linear history, merge-only so releases stay
   fast-forwards, no force pushes or deletions, admin bypass), `dev.json` (the same, with
   squash and rebase merges), `release-tags.json` (tags `v*` cannot be moved or deleted).
   Rulesets are not applied automatically from the repository; the import is a one-time
   manual step, and the templates are the record of what was configured. GitHub Actions'
   `integration_id` is 15368 and the *Repository admin* role is `actor_id` 5.
4. **Settings → Actions**: allow GitHub Actions; the release job needs `contents: write`,
   which the workflow requests explicitly.
5. **First CI run**: watch it. The App tests include STA tests that render with WPF; they
   pass on `windows-latest` in expectation but have not run there yet. If a test needs a
   desktop session, mark it with the `RequiresDesktop` trait and skip it in CI.
6. **Create issues** from the "Left open deliberately" list in `DEVELOPING.md` so the
   limitations are visible and can be picked up:
   - KDF phase not abortable (API.md states it; would be a contract change)
   - `OutOfMemoryException` during key derivation surfaces as a raw exception
   - No retry or cheaper-preset suggestion after a `ResourceLimit` refusal
   - Unlock card states the RAM requirement but does not warn when the machine cannot meet it
   - Side panes do not shrink below about 900 px window width (horizontal scrollbar)
   - Single-instance identity keys on the path; a mapped drive or junction alias counts as another instance
   - One unreproduced silent exit during UI automation (all exit paths now log first)
   - A signed release: unsigned executables trigger SmartScreen
7. **Release**: on `dev`, turn the *Unreleased* section of `CHANGELOG.md` into the new
   version and bump `<Version>` in `Directory.Build.props` by pull request; then
   `git checkout main && git merge --ff-only dev && git tag -a v1.0.0 -m "release: 1.0.0" && git push origin main --follow-tags`.
   The workflow creates a draft release with two zips and `SHA256SUMS.txt`; review and
   publish it. The branch model itself is described in `CONTRIBUTING.md`.
8. **Flip visibility to public** last, once the above is in place.

## 3. Why this license, and what it means

- The repository is published under PolyForm Noncommercial 1.0.0 from its first public
  commit; no code was ever released under a permissive license.
- **PolyForm Noncommercial 1.0.0** permits use, modification and redistribution for
  personal, educational, research, nonprofit and evaluation purposes, and forbids
  commercial use. It is a professionally drafted software license and has an SPDX
  identifier (`PolyForm-Noncommercial-1.0.0`).
- It is **not an open-source license** under the OSI or FSF definitions, because it
  discriminates by field of use. Describe Bastion Vault as *source-available, free for
  non-commercial use*. Consequences: GitHub does not auto-detect it (it shows "View
  license"), Linux distributions and most package registries will not carry it, and
  some companies forbid their staff from contributing to non-OSI projects.
- A commercial license can be offered separately. `CONTRIBUTING.md` reserves that right
  and requires DCO sign-off so all contributions arrive under compatible terms.
- The redistributed libraries are MIT, which imposes only an attribution requirement;
  `THIRD-PARTY-NOTICES.md` satisfies it. Combining MIT components inside a PolyForm-licensed
  work is permitted by MIT.

## 4. Export control: the "public domain" exemption explained

The formal self-classification, with the Annex I entry, the notes examined, the conditions
that keep it valid and a history table, is `docs/EXPORT-CONTROL.md`. This section is the
plain-language background.

Encryption software is a controlled dual-use item in principle. Two regimes matter:

**European Union** (Regulation (EU) 2021/821, the Dual-Use Regulation, which applies in
Germany). Its Annex I controls cryptographic software under category 5, part 2 (entry
5D002). However, the regulation's **General Software Note** and its definitions state that
controls do not apply to software that is *"in the public domain"*, defined as technology or
software *"made available without restrictions upon its further dissemination"*, and the
definition adds that **copyright restrictions do not remove software from the public
domain**. Publishing the complete source code in a public repository, where anyone can read,
copy and redistribute it, makes it "in the public domain" in this technical sense even
though it stays copyrighted and even though the license forbids commercial use (a copyright
restriction). Result: no export licence is needed to publish Bastion Vault's source or to let
people anywhere download it. Two caveats: the exemption covers what is actually published
(publish the source, not only binaries, and keep the release builds reproducible from it),
and it does not cover knowingly providing the software to a sanctioned party or for a
prohibited end use.

**United States**. GitHub is a US company, but the US rules (EAR §740.13(e) and §742.15(b))
place the obligation on the person publishing: US persons publishing "publicly available"
encryption source code must send a one-time notification email to the Bureau of Industry
and Security and the NSA. That obligation is not yours as a German publisher. GitHub's own
terms already cover their hosting of encryption source.

This is a summary of published regulations for a hobby project, not legal advice. If Bastion Vault
is ever sold commercially, the "public domain" exemption still covers the published source,
but the sold product is assessed on its own.
