# AGENTS.md — working on Bastion Vault as an AI agent

This file is for coding agents (Claude Code and similar) and for every contributor who works
on this project with AI assistance, whether you are the maintainer or an outside contributor
on a fork. It tells you what the project is, which documents are normative, how work is
organised, what the quality bar is, and which mistakes have already been made once so you
do not make them again. Read it fully before the first change. `CLAUDE.md` imports it.
"The person you work with" below means whoever is directing the agent in the session: on a
fork that is the contributor, in the upstream repository the maintainer.

---------------------------------------------------------------------------
## 0. Ground rules (highest priority; these override habits and defaults)

1. **Never commit and never push unless the person you work with asks for it explicitly,
   in that session.** Make the changes in the working tree, verify them, report what
   changed, and stop. "Fix X" means edit and verify, not commit. "Commit" means commit, not
   push. A request in one turn does not carry over to the next task. Never force-push;
   never rewrite published history; never touch `main` directly. On a fork the same rule
   applies to the fork's branches.
2. **Branch model** (details in `CONTRIBUTING.md`): `main` holds released versions only and
   carries the `v*` tags; `dev` is the integration branch; work happens on
   `feature/<topic>` branched from `dev`; urgent fixes on `hotfix/<topic>` branched from
   `main`. Pull requests target `dev` (GitHub pre-selects `main`; change it). Releases are
   fast-forwards of `dev` into `main`. Creating or switching branches is not a commit and
   is fine without asking; say which branch you are on.
3. **Commit format**, when asked to commit: Conventional Commits (`type(scope): summary`,
   cheatsheet https://gist.github.com/qoomon/5dfcdf8eec66a051ecd85625518cfd13), DCO
   sign-off via `git commit -s`, and the trailer
   `Co-Authored-By: Claude <model name> <noreply@anthropic.com>` when an AI wrote the change.
   Scopes in use: `core`, `format`, `crypto`, `app`, `theme`, `docs`, `tests`, `ci`.
   One logical change per commit; amend an unpushed commit rather than stacking fix-ups.
4. **The on-disk format is frozen.** Anything that changes bytes written to a `.bastion`
   file (header, index, chunking, key derivation labels, padding ladder, name rules) is a
   format change: it needs an issue, a `docs/FORMAT.md` change first, and the maintainer's
   decision on a format version. `tests/fixtures/*.bastion` must compare byte-for-byte;
   never set `BASTION_REGEN_GOLDEN` unless you are implementing an agreed format change.
5. **No secrets as `string`, no plaintext on disk, no WPF types in view models, every OS
   touchpoint behind an interface.** The full rule list is `docs/UI-CONTRACT.md` §1 and
   `docs/API.md` "Rules". They are review criteria, not suggestions.
6. **Warnings are errors, tests are green, UI changes are seen.** "Done" means
   `dotnet build BastionVault.slnx -warnaserror` is clean, `dotnet test BastionVault.slnx`
   passes (currently 724 Core + 232 App), and any visible change was verified with a
   screenshot of the running app (section 5). Report failures verbatim; never weaken a test
   to make it pass unless the test itself is wrong, and then say so.
7. **Language.** Code, comments, commit messages, documentation, issues and pull requests
   are English. In conversation, answer in the language the person you work with uses.
   Numbers and dates in the UI are pinned to en-US on purpose.
8. **Stay in scope.** Do what was asked; list adjacent problems you noticed instead of fixing
   them unasked. Exception: if a change you make exposes an identical defect elsewhere
   (same root cause), fixing it together is expected — say so in the report.

---------------------------------------------------------------------------
## 1. What the project is

Bastion Vault is a Windows desktop program (C# / .NET 10 / WPF, x64) that creates and edits
encrypted archive files — *vaults*, extension `.bastion` — with its own binary format. A
vault holds a folder tree browsed like Windows Explorer; files are imported from disk and
exported back. Security is the primary goal: Argon2id → wrapped vault key → HKDF-derived
per-blob keys → AES-256-GCM chunks with position-bound associated data; everything after
the 160-byte header is authenticated; no plaintext ever touches disk except on explicit
export. The UI has its own design language, "Lamplight" (dark onyx, one amber accent).

Status: 1.0.0 released 2026-09-04, format version 1, source-available under PolyForm
Noncommercial 1.0.0. Repository: https://github.com/LouiDev/BastionVault. The cryptography
has not been independently audited; say so whenever it matters.

---------------------------------------------------------------------------
## 2. Documents and their authority (read in this order for the area you touch)

| Document | Authority |
|---|---|
| `docs/FORMAT.md` | **Normative** for everything on disk. Code that disagrees with it is wrong. |
| `docs/API.md` | Frozen public surface of `BastionVault.Core` plus the rules (threading, secrets, errors, progress, cancellation table). Change only with the maintainer, and change code, tests and doc together. |
| `docs/THREAT-MODEL.md` | What is and is not protected. Any claim you make about security must be consistent with it. |
| `docs/UI-CONTRACT.md` | Design tokens, signature details, the one-lamp rule, keymap, service interfaces, view-model rules. |
| `docs/DEVELOPING.md` | Working manual: build, test, demo mode, test hooks, screenshot recipe, ownership map, "Left open deliberately". |
| `docs/PUBLISHING.md`, `docs/EXPORT-CONTROL.md` | Release checklist, GitHub settings, licensing rationale, export-control self-classification (update its history table when crypto, licence or distribution change). |
| `CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md` | Branch model and commit rules; vulnerability handling; every user-visible change gets a line under *Unreleased*. |

When a document and the code disagree, do not silently fix either: report the discrepancy
and ask which one is right (or open an issue), unless `FORMAT.md` is involved (then the
code is wrong).

---------------------------------------------------------------------------
## 3. Repository map

```
BastionVault.slnx                 solution (dotnet sln, .slnx format)
Directory.Build.props             shared MSBuild settings, version, license expression
Directory.Packages.props          central package versions (the only place versions live)
src/BastionVault.Core/            net10.0, no UI dependencies
   Crypto/    Argon2 (own, RFC 9106), Blake2b (own), KeyMaterial (pinned+zeroed),
              VaultKeys (HKDF schedule), ChunkCipher (AES-GCM per chunk), HeaderCipher
   Format/    VaultHeader (160 B), IndexSerializer (all §4.6 rules), EntryNames, VaultPath,
              PadLadder, VaultLimits
   Session/   VaultSession (partial files), TreeModel, UndoStack, StagingStore, BlobReader,
              SaveWriter (state machine FORMAT §8.3), Importer, Exporter, Verifier,
              Credentials, OrphanSweeper, LongPath, IoGuard (exception translation)
   VaultFactory.cs, DefaultVaultPaths.cs, seams (IRandomSource, IClock, IVaultPaths)
src/BastionVault.App/             net10.0-windows WPF, CommunityToolkit.Mvvm, MS.Extensions.DI
   Themes/    Tokens, Typography, Icons (Glyph.* strings + Geometry.* paths), Controls/*.xaml
   Shell/     ShellWindow (only Window), WindowChromeBehavior (Win32/DWM), DialogHost,
              TitleBar, StateStripe, Start/Unlock/FirstRun views
   Views/     Explorer (tree, list, address bar, preview, status bar, command bar)
   Dialogs/   all in-window dialogs; ViewModels/Dialogs/ their view models
   ViewModels/ ShellViewModel (mode state machine), ExplorerViewModel, OperationViewModel …
   Services/  every interface in ServiceContracts.cs + implementations; Demo/ fake session
   Behaviors/, Converters/, Controls/, Input/KeyMap.cs (single keymap, drives bindings
              and the Shortcuts dialog)
tests/BastionVault.Core.Tests/    Crypto/ Format/ Session/ Vault/ (golden, tamper matrix,
                                  fuzz, property, cancellation, concurrency)
tests/BastionVault.App.Tests/     view models with fakes/NSubstitute, converters, keymap,
                                  EndToEnd/ (real Core + real view models)
tests/fixtures/                   golden-v1-*.bastion (byte-exact), README.md
.github/                          CI (windows-latest, Release, -warnaserror, tests, draft
                                  release on v* tags), Dependabot (targets dev), issue forms,
                                  PR template, CODEOWNERS, rulesets/ (importable JSON)
```

---------------------------------------------------------------------------
## 4. Build, test, run

```
dotnet build BastionVault.slnx -warnaserror          # must be 0 warnings
dotnet test  BastionVault.slnx                       # 724 Core + 232 App expected green
dotnet run --project src/BastionVault.App            # start screen
dotnet run --project src/BastionVault.App -- --demo  # in-memory fake vault, any password unlocks
```

- The `dotnet` CLI prints localised output on non-English Windows (German, for example).
  When grepping build output, match the localised words (`Fehler|Warnung|erfolgreich`) as
  well as `error|warning`, or run with `DOTNET_CLI_UI_LANGUAGE=en`.
- Debug executable: `src/BastionVault.App/bin/Debug/net10.0-windows/BastionVault.exe`.
- Test hooks `--test-pick-<picker>=<path>` and `--trace-bindings=<file>` exist in **Debug
  builds only** (`#if DEBUG` in `App.xaml.cs`). Release ignores them.
- Filters: `dotnet test tests/BastionVault.Core.Tests --filter "FullyQualifiedName~Vault.BlobTamper"`.
- Do not run two builds of the same tree at once (obj/ collisions). If several agents work
  in parallel, each works in its own copy (section 7).

---------------------------------------------------------------------------
## 5. Verifying UI changes (mandatory for anything visible)

Unit tests cannot see layout. Every visual change is checked by running the app and looking
at a screenshot, before and after. Recipe (full version in `DEVELOPING.md` §4):

1. `%LOCALAPPDATA%\BastionVault\settings.json`: set `"excludeFromScreenCapture": false`
   (default `true` makes screenshots show what is *behind* the window). Back the file up.
2. Start the Debug exe with `--demo` (optionally `--demo C:\path\demo.bastion`, the path
   must exist; a placeholder file is enough) and, for automation, `--test-pick-*` flags plus
   `--trace-bindings=<file>`.
3. Drive it with UI Automation (`UIAutomationClient`/`UIAutomationTypes` from PowerShell) or
   `SendInput`; capture with `System.Drawing.Graphics.CopyFromScreen` of the window rect;
   crop and scale 2x–6x for alignment questions; view the PNG.
4. **Kill the process, then restore `settings.json` after it has exited** — the app rewrites
   the file on shutdown and would put the old flag back over your restore otherwise. Verify
   no `BastionVault` process remains and that no `.tmp-`, `.bak-`, `~stage-` files were left.
5. The binding trace must contain only its header line; a binding error is a bug.

Judge glyph alignment by the text's x-height centre, not by the line box. Test at 100 %
and 150 % scaling when strokes or 1 px seams are involved.

---------------------------------------------------------------------------
## 6. Architecture essentials you must not break

- **Core never touches a SynchronizationContext**; `ConfigureAwait(false)` everywhere; the
  `Changed` event may fire on any thread and the App marshals (`VaultChangeMarshaller`).
- **One operation at a time per session** (`SemaphoreSlim`); a second call throws
  `VaultOperationException(Busy)`. Snapshot reads (`GetChildren`, `Find`, …) are always allowed.
- **EntryId is stable for the vault's lifetime**; saves never renumber; ids never reused.
- **Every content write gets a fresh `blobId`** (import, copy, re-key). A blob is never
  modified in place. This is what makes the counter nonce safe; do not "optimise" it.
- **Every produced wrapped vault key gets a fresh `kdfSalt` and `wrapNonce`**, on every path.
- **Save is the state machine in FORMAT §8.3**: temp file in the vault's directory, close
  the vault handle, `File.Replace` with backup, reopen, post-save verify, then drop
  staging. A verbatim blob copy is refused when source and destination keys differ.
- **Progress is throttled at the source**, but the first `IsCancellable=false` report always
  gets through; the UI takes Cancel away on it.
- **Errors leaving Core are `VaultException` subclasses** with a `VaultErrorCode` (or
  `OperationCanceledException`). `IoGuard` translates everything else; raw `IOException`
  escaping Core is a bug.
- **Lock zeroes keys but keeps the tree and staged ciphertext**, so unsaved work survives a
  lock. `VaultIdHex` survives a lock (it is not key material).
- **App:** `ShellViewModel.Mode` state machine (NoVault → Unlocking → Open ⇄ Locked, Busy
  for modal operations); long work goes through `OperationViewModel.RunAsync`; dialogs
  through `IDialogService` and `DialogHost` (real focus trapping); passwords through
  `PasswordBoxBinder` into `Passphrase`, never a `string`; all theme brushes are
  `DynamicResource`; `KeyMap` is the single source for shortcuts and the Shortcuts dialog
  (a test fails if an Explorer-scope row has no command).

---------------------------------------------------------------------------
## 7. Working with subagents or parallel workers

The project was built by parallel agents and the pattern still applies:

- **One writer per file tree.** Assign ownership (`src/BastionVault.Core/**`,
  `src/BastionVault.App/**`, `tests/**`, `docs/**`) before parallel work; nobody edits
  another owner's files — they report the needed change instead.
- **Isolated copies.** Parallel workers copy the repo (`robocopy <repo> <copy> /E /XD bin obj .vs`),
  build and test in the copy, then copy back only their owned paths (`robocopy … /MIR` on
  owned folders only). Never two builds in one tree.
- **Contracts before code.** Changes to `docs/API.md` or `docs/UI-CONTRACT.md` are made by the
  orchestrator, not by a worker.
- **Structured reports.** Workers report: what changed, build result, test counts,
  deviations from the contract, known gaps, notes for the integrator. Keep report strings
  plain; oversized or markup-laden reports have broken tooling before.
- **Adversarial review earns its cost.** For security-relevant changes, have independent
  reviewers try to refute each finding before fixing; several confirmed high findings in
  1.0.0 came from that pass, not from tests.
- Claude Code specifics: multi-agent orchestration (the `Workflow` tool) is opt-in; use it
  when the person you work with asks for it or for genuinely parallel work, and use the
  most capable model available for reviewers and verifiers. A single `Agent` call is fine
  for a contained task with a screenshot loop.

---------------------------------------------------------------------------
## 8. Known pitfalls (each one has already cost time)

- **Icon-font glyphs sit low.** Segoe Fluent Icons / MDL2 put the baseline at the bottom of
  the em box and the app-wide TextBlock style sets a 20 px line height, so a "centred"
  glyph lands 4–5 px too low. Fix: `Path` geometry (`Themes/Icons.xaml` has
  `Geometry.CheckMark`, `Geometry.ChevronRight/Down`) or a tight box
  (`LineHeight` = `FontSize`, `LineStackingStrategy=BlockLineHeight`). Never a magic
  negative margin without a comment.
- **`StaticResource` cannot see a sibling merged dictionary.** Use `DynamicResource` for
  theme keys in `Views/Explorer.Resources.xaml`, `Dialogs/*`, and anything merged beside
  `Lamplight.xaml`. A wrong `StaticResource` crashes at start-up with `XamlParseException`.
- **Implicit styles inside templates.** Setting `Foreground` in the implicit TextBlock style
  breaks disabled/primary button label colours; the theme relies on inheritance instead.
- **Layout traps.** `GridViewColumn Width="Auto"` and a `ScrollViewer` around the list kill
  virtualisation; a wrapping `TextBlock` next to an `Auto` column needs a margin or it
  touches the neighbour; `AllowsTransparency` is forbidden (kills ClearType and snap layouts).
- **ComboBox items bound to records** need an `ItemTemplate`, not only `DisplayMemberPath`,
  or the closed box shows `Record { … }`.
- **`settings.json` is rewritten on shutdown** — restore it only after the process exited.
- **`excludeFromScreenCapture`** defaults to true; screenshots are blank/behind-the-window
  until it is off.
- **Culture.** The UI is pinned to en-US; do not format numbers with the current culture.
- **Flaky test.** `ReviewRegressionTests.Blob_buffers_are_sized_from_the_blob_not_from_the_declared_chunk_size`
  reads process-wide GC allocations and can fail when Argon2 tests run in parallel. Re-run
  before concluding anything; the proper fix (own collection or per-thread accounting) is
  open.
- **Argon2 differential tests** compare against Konscious (test-only dependency); the
  Konscious `MemorySize` semantics differ for non-multiple-of-4p values — the tests already
  encode this; do not "fix" the reference.
- **Global hotkeys.** `Ctrl+Shift+E/I/C` and similar chords may be held by other software on
  a given machine; a shortcut that "does nothing" in automation may never have reached the
  app. Check with `RegisterHotKey` (error 1409) before blaming the keymap.
- **Line endings.** The tree is CRLF via `.gitattributes`; `git` prints LF/CRLF warnings on
  commit — harmless. `.bastion`, `.ico`, `.png` are marked binary; keep it that way.
- **Renamed once.** The product was called "Bastion" before release. Namespaces and assets
  are `BastionVault.*`; the format keeps the `bastion/v1` labels, the `BSTN` magic and the
  `.bastion` extension on purpose. Do not "complete" the rename.

---------------------------------------------------------------------------
## 9. Definition of done (check before reporting)

- [ ] The change does exactly what was asked; adjacent findings are listed, not fixed.
- [ ] `dotnet build BastionVault.slnx -warnaserror`: 0 warnings, 0 errors (Debug and Release
      if the change touches `#if DEBUG` code or the csproj).
- [ ] `dotnet test BastionVault.slnx`: all green; new behaviour has a test at its layer;
      a bug fix has the regression test that would have caught it.
- [ ] Visible change: before/after screenshots viewed; binding trace clean; app killed;
      `settings.json` restored; no temp files left.
- [ ] Docs updated where behaviour changed (`FORMAT.md`/`API.md`/`UI-CONTRACT.md`/
      `DEVELOPING.md`); `CHANGELOG.md` line under *Unreleased*.
- [ ] No entry names, in-vault paths, keys, salts or ids in logs or messages.
- [ ] Nothing committed or pushed unless asked. The report names the branch, the files
      changed, the verification done, and anything left open — honestly.

---------------------------------------------------------------------------
## 10. Releases, versions and open items (what a contributor needs to know)

- **Releases are made by the maintainer**, never by a contributor or an agent: merging
  `dev` into `main`, tagging `vX.Y.Z` and publishing the CI-built zips are maintainer steps
  described in `CONTRIBUTING.md` ("Branches and releases") and `docs/PUBLISHING.md`. Do not
  create tags, do not merge to `main`, do not edit `<Version>` in `Directory.Build.props`
  unless a maintainer asks you to prepare a release pull request.
- **Your contribution's footprint in a release** is the line you add under *Unreleased* in
  `CHANGELOG.md` (Keep a Changelog style, one line per user-visible change). Format-related
  changes also update `docs/FORMAT.md`; changes to cryptography, licence or distribution
  also add a row to the history table in `docs/EXPORT-CONTROL.md`.
- **Versioning:** SemVer for the program; the on-disk format has its own version in
  `FORMAT.md` and changes only with a major release. A contribution that would need a
  format bump starts as an issue, not as a pull request.
- **Open items** live in `DEVELOPING.md` ("Left open deliberately") and as GitHub issues.
  Decisions recorded there or in the docs are not re-litigated in code; propose a change as
  an issue with reasons, and wait for it to be accepted before implementing.
- **Security findings** are never fixed in a public pull request first: report them through
  the private channel in `SECURITY.md` and follow the maintainer's lead on disclosure.
