# Bastion Vault

Bastion Vault is a Windows desktop program (C# / .NET 10 / WPF) that creates and edits
**encrypted archive files** — *vaults*, extension `.bastion` — with its own binary
format. A vault holds a folder tree you browse like Windows Explorer; files and folders
are imported from disk and exported back. Everything inside the vault — content, names,
folder structure, sizes, entry count — is encrypted and authenticated.

> One lamp in a stone room. Bastion Vault's dark "Lamplight" theme uses a single amber accent
> only where something is live, focused or unsaved.

**Status:** version 1.0, format version 1. Source-available, free for non-commercial use
(see [License](#license)). The cryptography has **not been independently audited**; see
[SECURITY.md](SECURITY.md) before trusting it with anything you cannot afford to lose.

## Security in one paragraph

Password → **Argon2id** (memory-hard, default 512 MiB) → key-encryption key → unwraps a
random **vault key** → HKDF-derived per-blob keys → **AES-256-GCM** on every 1 MiB chunk,
with the vault/blob/position bound as associated data. The header carries no vault
identifier and no hint whether a keyfile is used. Every byte after the 160-byte header is
authenticated; blobs must tile the data section exactly; the file length must match the
header and index exactly. Tampering, truncation, chunk reordering, splicing from another
version, and KDF downgrade are all detected before any plaintext is released. An optional
**keyfile** acts as a second factor. Saves are atomic (write temp, `File.Replace`, verify),
deleted content is really gone on save, imported content is encrypted before it is staged,
and keys are zeroed on lock. Details: [docs/FORMAT.md](docs/FORMAT.md) and
[docs/THREAT-MODEL.md](docs/THREAT-MODEL.md).

## Features

- Create / open / lock / auto-lock vaults; change password (full re-key by default), add or
  remove a keyfile, change the KDF cost.
- Explorer-like navigation: folder tree, details list with natural sort, breadcrumb /
  editable address bar, back/forward history, search, multi-select, F2 rename, drag & drop
  from Explorer (import) and inside the vault (move), internal cut/copy/paste, undo/redo.
- Import files and folders (streamed, reparse points skipped, conflict handling), export
  selection or everything (timestamps restored, Mark-of-the-Web), in-memory preview of
  text and images, hex view for everything else.
- Verify (every chunk, every blob hash, layout), Recover (salvage everything that still
  authenticates from a damaged vault), Properties with live KDF parameters, save counter and
  "last saved".
- Pending-change vocabulary (status rail, state stripe, title bullet), no confirmation on
  delete — it is undoable until you save.
- Optional size obfuscation and re-encrypt-on-save for people whose vaults live in synced
  folders.

## Deliberately not in v1

Drag-out to Explorer, content search, editing a file in place ("open with"), incremental
saves, a second cipher, whole-file MAC. See the threat model for why. Known limitations
that are tracked as issues are listed in [docs/DEVELOPING.md](docs/DEVELOPING.md).

## Install

Download the latest release from the Releases page. Two variants are published:
`framework-dependent` (needs the .NET 10 Desktop Runtime) and `selfcontained` (nothing to
install). The executables are not code-signed, so Windows SmartScreen warns on first start;
verify the download against `SHA256SUMS.txt`.

## Build from source

```
dotnet build BastionVault.slnx
dotnet test  BastionVault.slnx
dotnet run --project src/BastionVault.App
```

Requires the .NET 10 SDK on Windows 10/11 (x64). Solution layout:

```
src/BastionVault.Core          format, crypto, session (no UI)
src/BastionVault.App           WPF application ("Lamplight" theme)
tests/BastionVault.Core.Tests  vectors, golden files, tamper matrix, fuzzing
tests/BastionVault.App.Tests   ViewModel tests with a fake session, real-Core end-to-end test
docs/                     FORMAT.md (normative), API.md, THREAT-MODEL.md, UI-CONTRACT.md,
                          DEVELOPING.md, PUBLISHING.md, EXPORT-CONTROL.md
```

[docs/DEVELOPING.md](docs/DEVELOPING.md) has the working manual: demo mode, the UI-automation
and screenshot workflow, how to regenerate the golden fixtures, the ownership map and the
known limitations.

## Contributing

Contributions are welcome under the terms in [CONTRIBUTING.md](CONTRIBUTING.md): Conventional
Commits, DCO sign-off, warnings as errors, tests with every change, and no format or
cryptography change without a spec change first. Please be kind; see
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

Branches: `main` holds released versions only and carries the `v*` tags; development happens
on `dev`, so **open pull requests against `dev`**. `feature/*` branches are merged into `dev`,
and `dev` is fast-forwarded into `main` when a release ships. Security fixes take the
`hotfix/*` route straight to `main` and a patch release.

## Security

Report vulnerabilities privately through GitHub's *Report a vulnerability* form, never in a
public issue. Process and scope: [SECURITY.md](SECURITY.md).

## License

Bastion Vault is licensed under the **PolyForm Noncommercial License 1.0.0** — see
[LICENSE](LICENSE). In short: you may use, study, modify and share Bastion Vault for any
non-commercial purpose (personal, educational, research, nonprofit, evaluation); commercial
use is not permitted under this license. This is a *source-available* license, not an
open-source license in the OSI sense. For a commercial license, open an issue titled
"commercial license" and the maintainer will get in touch.

Required Notice: Copyright (c) 2026 LouiDev (https://github.com/LouiDev/BastionVault)

Redistributed third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Notes

- Bastion Vault is not a backup tool. A vault is one file; back it up like any other file.
- A save rewrites the whole vault. Vaults in the low-gigabyte range are the design target.
- There is no password recovery. None.
