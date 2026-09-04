# Changelog

All notable changes to Bastion Vault are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). The on-disk **format version** is tracked
separately in `docs/FORMAT.md` and only changes with a major release.

## [Unreleased]

### Added
- Branch model: `main` holds releases only, `dev` is the integration branch, work happens on
  `feature/*`, urgent fixes on `hotfix/*`. Documented in `CONTRIBUTING.md`; CI and Dependabot
  target `dev`; ruleset templates for `main`, `dev` and `v*` tags in `.github/rulesets/`.

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

[Unreleased]: https://github.com/LouiDev/BastionVault/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/LouiDev/BastionVault/releases/tag/v1.0.0
