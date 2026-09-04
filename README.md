# Bastion

Bastion is a Windows desktop program (C# / .NET 10 / WPF) that creates and edits
**encrypted archive files** — *vaults*, extension `.bastion` — with its own binary
format. A vault holds a folder tree you browse like Windows Explorer; files and folders
are imported from disk and exported back. Everything inside the vault — content, names,
folder structure, sizes, entry count — is encrypted and authenticated.

> One lamp in a stone room. Bastion's dark "Lamplight" theme uses a single amber accent
> only where something is live, focused or unsaved.

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
saves, a second cipher, whole-file MAC. See the threat model for why.

## Build

```
dotnet build Bastion.slnx
dotnet test  Bastion.slnx
dotnet run --project src/Bastion.App
```

Requires the .NET 10 SDK on Windows 10/11 (x64). Solution layout:

```
src/Bastion.Core        format, crypto, session (no UI)
src/Bastion.App         WPF application ("Lamplight" theme)
tests/Bastion.Core.Tests  vectors, golden files, tamper matrix, fuzzing
tests/Bastion.App.Tests   ViewModel tests with a fake session
docs/                   FORMAT.md (normative), API.md, THREAT-MODEL.md, UI-CONTRACT.md
```

[docs/DEVELOPING.md](docs/DEVELOPING.md) has the working manual: demo mode, the UI-automation
and screenshot workflow, how to regenerate the golden fixtures, the ownership map and the
known limitations.

## Notes

- Bastion is not a backup tool. A vault is one file; back it up like any other file.
- A save rewrites the whole vault. Vaults in the low-gigabyte range are the design target.
- There is no password recovery. None.
