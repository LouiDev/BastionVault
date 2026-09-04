# Changelog

All notable changes to Bastion are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). The on-disk **format version** is tracked
separately in `docs/FORMAT.md` and only changes with a major release.

## [Unreleased]

### Changed
- License changed from MIT to PolyForm Noncommercial 1.0.0 before the first public release.
- Test hooks (`--test-pick-*`, `--trace-bindings`) are compiled into Debug builds only.

### Added
- Repository hygiene for publication: `SECURITY.md`, `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `THIRD-PARTY-NOTICES.md`, `NOTICE`, CI workflow, Dependabot,
  issue and pull request templates, `docs/PUBLISHING.md`.

## [1.0.0] - 2026-09-04

First complete version. Format version 1.

### Added
- `Bastion.Core`: `.bastion` vault format (Argon2id → wrapped vault key → HKDF per-blob
  keys → AES-256-GCM chunks with position-bound associated data), authenticated index with
  a redundant copy, exact-tiling and length checks, own Argon2 (RFC 9106) and BLAKE2b
  (RFC 7693) implementations, atomic save state machine, encrypted staging, streamed
  import/export, verify, recover, undo/redo, lock/unlock, credential change with full
  re-key, save-as-copy, size obfuscation, rollback counter.
- `Bastion.App`: WPF application with the Lamplight theme, custom window chrome,
  Explorer-style tree/list/preview, editable address bar with history, drag and drop,
  internal clipboard, keyboard map, in-window dialogs, auto-lock, screen-capture
  exclusion, keyfile second factor, DPAPI-protected recent list and rollback guard.
- Tests: RFC vectors, differential Argon2 tests, golden fixtures, exhaustive tamper
  matrix, index and header fuzzing, property tests, cancellation and concurrency
  contracts, view-model tests, a real-Core end-to-end test.
- Documentation: `docs/FORMAT.md`, `docs/API.md`, `docs/THREAT-MODEL.md`,
  `docs/UI-CONTRACT.md`, `docs/DEVELOPING.md`.

[Unreleased]: https://github.com/LouiDev/Bastion/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/LouiDev/Bastion/releases/tag/v1.0.0
