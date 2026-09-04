# Bastion Vault threat model (honest version)

Bastion Vault protects the *contents* of a vault file against someone who obtains the file.
It does not protect against someone who controls the machine while the vault is open.

## Adversaries and what they get

### A1 — Single snapshot of the `.bastion` file (theft, cloud copy, USB)
Learns: total file size (≈ total plaintext volume + 16 B per MiB + two padded index
copies), the padded index size (a coarse log2 bucket of the entry count, because the
index is padded on a 64 KiB / power-of-two ladder), and the Argon2id cost parameters.
Does **not** learn: file names, folder structure, per-file sizes, exact entry count,
whether a keyfile is used, or any stable identifier (the header holds no vault id; the
salt and wrapped key are constant between saves but change on every password change).
With "size obfuscation" enabled the data section is padded so total size is coarse too.

### A2 — Multiple snapshots of the same vault (sync version history, backups)
Learns additionally: which byte ranges changed between versions (unchanged blobs are
copied verbatim, so the change pattern and the exact ciphertext length of changed or
added files are visible). Mitigation: "Re-encrypt everything on save" (a `Rekey` save)
makes every version look fresh at the cost of a full rewrite; also size obfuscation.
Cannot: splice a chunk or blob from an older version into a newer one — every content
write uses a fresh per-blob key, and the index commits to each blob's hash.
**Residual:** whole-file rollback (handing you a complete, valid, older vault) cannot be
detected inside the file. The index carries a `saveCounter` and "last saved" time; the
UI shows them at unlock and keeps a per-machine record of the highest counter seen to
warn on a decrease. That record is keyed on the derived `vaultId` (FORMAT.md 2.4,
`IVaultSession.VaultIdHex`), never on the path, so an older copy is recognised wherever it
is handed over — under another name, from a USB stick, restored into another folder.

### A3 — Offline password guessing
Cost per guess = one Argon2id at the stored parameters (default 512 MiB, 3 passes) plus
one AES-GCM unwrap. There is no separate verifier; the GCM tag of the wrapped vault key
is the only oracle. An optional keyfile (hashed into the KEK) makes guessing impossible
without it. A keyfile stored next to the vault or with its path remembered in settings
is *not* a second factor, only a longer password — the UI says so.

### A4 — Tampering (bit flips, truncation, reordering, splicing, downgrade, crafted files)
Every byte after the header is inside an AES-GCM tag or is padding whose length is
authenticated. Header fields are bound as AAD to the key wrap and to the index. Chunks
are bound to (vault, blob, position, last-flag). Blobs must tile the data section
exactly; the file length must match the header and index exactly. Any modification is
detected before unauthenticated plaintext is released to the user or to disk; a
partially written export is deleted on failure. A crafted vault opened with its
(attacker-supplied) password cannot make the reader allocate more than the documented
limits, recurse, or follow reserved/relative names on export (see FORMAT.md §6 and §7).
A damaged primary index falls back to the authenticated index copy.

### A5 — Traces on the local machine
Bastion Vault never writes plaintext except on explicit export. Imported content is
encrypted before it is staged (in memory up to 64 MiB, otherwise in a single
delete-on-close container). Bastion Vault does not add vaults to Windows Recent Items or
jump lists; its own recent-vault list is opt-out and stored under DPAPI (current user).
Logs contain no entry names, paths inside the vault, keys or ids. **Residual:** the
vault's own file name and modification time; the `.bastion` extension; the OS file
dialogs' MRU; screen capture / Recall while the vault is shown (mitigated with
`WDA_EXCLUDEFROMCAPTURE` by default; a setting turns it off for screen sharing).

### A6 — Memory (process memory, pagefile, hibernation, crash dumps)
Best effort, not a guarantee: keys live in pinned buffers that are zeroed on lock,
close, crash-handler and dispose; the Argon2 working memory is zeroed after each
derivation (own implementation); passwords are read from `PasswordBox.SecurePassword`
into pinned UTF-8 buffers, never into `string` (NFC normalisation of a non-ASCII
password creates one transient managed string, which cannot be zeroed). Not zeroable:
WPF `PasswordBox` internals, decoded preview bitmaps (unmanaged WIC buffers), text shown
on screen, entry names in the in-memory tree while the vault is open or soft-locked. Bastion Vault
does not disable the pagefile or Windows Error Reporting for you.

## Explicitly out of scope
Malware or a keylogger on the machine while a vault is unlocked; secure deletion on
SSDs (deleted entries vanish from the file on save, but the old file's sectors may
persist physically); side channels beyond constant-time tag comparison (provided by
CNG); deniability; multi-user concurrent editing (two machines saving the same synced
vault produce a detected conflict, not a merge).

## Deliberate v1 non-goals
Drag-out to Explorer (it would need plaintext temp files or a custom COM data
object), content search (would require decrypting everything per query), in-place
editing / "open with default app" (writes plaintext to disk), incremental save
(reserved for a later format version), a second cipher, whole-file MAC/trailer (per-part
AEAD plus the length equation gives the same guarantee without a full read on open).
