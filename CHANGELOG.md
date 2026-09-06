# Changelog

All notable changes to Bastion Vault are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). The on-disk **format version** is tracked
separately in `docs/FORMAT.md` and only changes with a major release.

## [Unreleased]

### Added
- The unlock card now warns before the button is pressed when this PC cannot serve the vault's
  key derivation (installed memory vs. the vault's requirement), and disables Unlock in that
  state instead of letting the refusal arrive after the click (#13).
- After a key derivation that passed the pre-flight but could not get its memory, the unlock
  card keeps the figures (needed vs. free), does not count it as a failed attempt and offers
  *Try again*; the New vault and Change password dialogs mark a preset this PC cannot serve,
  preselect the largest one that fits and say which one that is (#17).
- `KdfPreflight` in `BastionVault.Core`: the FORMAT.md §3.1 step 9 memory check as a public
  question, so the UI shows Core's verdict rather than its own.
- Debug-only test hooks `--test-installed-memory=<bytes>` (screenshot the memory states) and
  `--test-crash` (screenshot the crash window).
- The entry list's columns can be dragged into a new order, and the order is remembered with
  the widths and the sort; a remembered layout that names a column this build no longer has
  is ignored (#23).
- `docs/INSTALL.md`: installation guide for the zip releases (choosing a variant, verifying
  the download, the SmartScreen prompt, the optional `.bastion` file type and why the program
  folder must not move afterwards, what is written under `%LOCALAPPDATA%`, updating, complete
  removal, command line). Linked from `README.md` and from the release notes the CI drafts.

### Fixed
- The same vault reached through a junction, a mapped drive letter or a UNC path is now one
  vault for the single-instance check: the lock is keyed on the file id once the file exists,
  and on the path only until then (#20).
- An `OutOfMemoryException` from the Argon2 block allocation no longer escapes
  `BastionVault.Core` as itself (API.md rule 5): it leaves as `ResourceLimit` with the figures,
  and the App shows a message instead of its crash handler (#16).

### Changed
- The explorer's side panes follow the window width: above 1180 px they keep the widths the
  user set, down to 1000 px they shrink in proportion, and below that the preview folds away
  until the window is wider or the user asks for it; the four list columns keep their room at
  the 880 px minimum instead of a horizontal scrollbar (#18).
- The hex preview puts 8 bytes on a line when the pane is too narrow for 16, so the ASCII
  column fits in the default pane (#19).
- The crash handler shows a Bastion Vault window with "Continue" and "Exit" instead of the
  native message box whose buttons followed the OS language (#24).
- While a vault is locked the window title keeps its name and appends "(locked)", agreeing
  with the vault chip in the title bar (#25).
- CI no longer runs a separate build for pushes to `main`; the `v*` tag pushed alongside
  already builds, tests and packages that commit. Pull requests against `main` are unchanged.
- The log now accounts for every way a run ends, not only crashes: `Starting` and `Exiting
  with code N` lines carry the process id, each close names what triggered it (title bar,
  Exit command, Alt+F4 or system menu, or a close message from another program), a
  Windows log-off or shutdown is recorded, and the log and the crash handlers are installed
  before anything else in start-up. Follow-up to the one unexplained exit during the 1.0.0
  automation run (#21).

## [1.0.1] - 2026-09-04

### Fixed
- Glyph alignment: the check mark of a checked check box and of a checked menu item, and the
  chevrons in the breadcrumb, the submenu arrow, the folder tree, the expander and the combo
  box, are now optically centred against the text beside them. Icon-font glyphs were being
  placed by the 20 px Body line box, which put them up to five pixels low; the marks that
  have to line up are drawn as `Path` geometry, and the remaining glyph and text boxes carry
  a line box tight to their font size.
- Menu items: icon, header and shortcut share one vertical centre line.
- Settings dialog: the caption of the file-type row keeps clear of the button beside it.
- Every em dash in user-visible strings, XAML and code comments is a plain hyphen.

### Changed
- Branch model: `main` holds releases only, `dev` is the integration branch, work happens on
  `feature/*`, urgent fixes on `hotfix/*`. Documented in `CONTRIBUTING.md`; CI and Dependabot
  target `dev`; ruleset templates for `main`, `dev` and `v*` tags in `.github/rulesets/`.
- `AGENTS.md` briefs AI-assisted contributors on the project's rules and pitfalls;
  `CLAUDE.md` imports it.

## [1.0.0] - 2026-09-04

First public, source-available release under the PolyForm Noncommercial License 1.0.0.
Format version 1.

### Added
- `BastionVault.Core`: `.bastion` vault format (Argon2id → wrapped vault key → HKDF per-blob
  keys → AES-256-GCM chunks with position-bound associated data), authenticated index with
  a redundant copy, exact-tiling and length checks, own Argon2 (RFC 9106) and BLAKE2b
  (RFC 7693) implementations, atomic save state machine, encrypted staging, streamed
  import/export, verify, recover, undo/redo, lock/unlock, credential change with full
  re-key, save-as-copy, size obfuscation, rollback counter.
- `BastionVault.App`: WPF application with the Lamplight theme, custom window chrome,
  Explorer-style tree/list/preview, editable address bar with history, drag and drop,
  internal clipboard, keyboard map, in-window dialogs, auto-lock, screen-capture
  exclusion, keyfile second factor, DPAPI-protected recent list and rollback guard.
- Tests: RFC vectors, differential Argon2 tests, golden fixtures, exhaustive tamper
  matrix, index and header fuzzing, property tests, cancellation and concurrency
  contracts, view-model tests, a real-Core end-to-end test.
- Documentation: `docs/FORMAT.md`, `docs/API.md`, `docs/THREAT-MODEL.md`,
  `docs/UI-CONTRACT.md`, `docs/DEVELOPING.md`, `docs/PUBLISHING.md`,
  `docs/EXPORT-CONTROL.md`.
- Project files: `SECURITY.md`, `CONTRIBUTING.md` (Conventional Commits, DCO),
  `CODE_OF_CONDUCT.md`, `THIRD-PARTY-NOTICES.md`, `NOTICE`, CI workflow with draft
  releases, Dependabot, issue and pull request templates. Test hooks are compiled into
  Debug builds only.

[Unreleased]: https://github.com/LouiDev/BastionVault/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/LouiDev/BastionVault/releases/tag/v1.0.1
[1.0.0]: https://github.com/LouiDev/BastionVault/releases/tag/v1.0.0
