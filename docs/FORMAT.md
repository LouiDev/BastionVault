# Bastion Vault Format — version 1 (normative)

This document is the single source of truth for the `.bastion` file format and for
the on-disk behaviour of a conforming writer. `BastionVault.Core` implements exactly
this; `BastionVault.Core.Tests` asserts against it. Where this document and code
disagree, the code is wrong.

Conventions: all integers are little-endian. `u8/u16/u32/u64/i64` are unsigned /
signed integers of that width. `bN` is an N-byte opaque field. "MUST" rules are
enforced by the reader; a violation is rejected with the listed `VaultErrorCode`
**before** any plaintext or any tree node is exposed.

---------------------------------------------------------------------------
## 1. Overview

```
+------------------------------+  offset 0
| Header            (plaintext)|  160 bytes (headerLength)
+------------------------------+  offset headerLength
| Index             (AEAD)     |  indexLength bytes  (ciphertext + 16 B tag)
+------------------------------+  offset headerLength + indexLength
| Data section      (AEAD)     |  dataSectionLength bytes (from the index)
+------------------------------+
| Index copy        (AEAD)     |  indexLength bytes  (same plaintext, 2nd nonce)
+------------------------------+  = fileLength
```

**Length equation (MUST, checked at open, `Truncated`):**

    fileLength == headerLength + 2 * indexLength + dataSectionLength

Before the KDF runs the reader checks the cheap half: `fileLength >= headerLength + 2 * indexLength`.
After the index is decrypted it checks the equation exactly. Trailing bytes are rejected.

Everything after the header is either an authenticated index (or its authenticated
copy), a chunk inside an authenticated blob, or data-section padding whose length is
authenticated by the index. There is no plaintext framing anywhere after the header;
the data section is indistinguishable from random bytes.

---------------------------------------------------------------------------
## 2. Cryptographic primitives

| Purpose            | Algorithm                                              |
|--------------------|--------------------------------------------------------|
| Password KDF       | Argon2id (RFC 9106), tag length 32, version 0x13        |
| Key expansion      | HKDF-SHA256 (RFC 5869); `Expand` only where noted      |
| AEAD               | AES-256-GCM, 12-byte nonce, 16-byte tag (pinned)       |
| Keyfile digest     | HMAC-SHA256                                            |
| Blob commitment    | SHA-256                                                |
| RNG                | OS CSPRNG (`RandomNumberGenerator`) via `IRandomSource`|

`cipherId = 2` is reserved for ChaCha20-Poly1305 and is **not** implemented in v1
(a v1 reader rejects it with `UnsupportedParameters`).

### 2.1 Password bytes

    pw = UTF8( NFC( passwordString ) )

- No NUL terminator, no trimming, no case folding.
- Strings containing unpaired surrogates are rejected (`NameInvalid`-class argument
  error at the API boundary, before normalisation).
- 1 <= len(pw) <= 1024 bytes. (The UI additionally requires >= 8 characters for new vaults.)

### 2.2 Keyfile digest

    kf = HMAC-SHA256( key = "bastion/v1/keyfile", msg = keyfileBytes )       (32 bytes)

- Keyfile length MUST be 1 .. 1 MiB (1 048 576 bytes); otherwise `UnsupportedParameters`.
- When no keyfile is used, `kf` is the empty byte string and `keyfilePresent = 0`.
- The header carries **no** indication of whether a keyfile is required.

### 2.3 Key encryption key (KEK)

    a2  = Argon2id( password = pw, salt = kdfSalt(32), m = kdfMemoryKiB,
                    t = kdfIterations, p = kdfParallelism, tagLength = 32 )
    KEK = HKDF-SHA256( ikm = a2 || kf, salt = kdfSalt, info = "bastion/v1/kek" || u8 keyfilePresent, L = 32 )

The `keyfilePresent` byte (0 or 1) domain-separates "no keyfile" from "a keyfile".

### 2.4 Vault key and derived keys

    VaultKey  = 32 random bytes, generated at vault creation and at every re-key
    vaultId   = HKDF-Expand( prk = VaultKey, info = "bastion/v1/vaultid", L = 16 )
    IndexKey  = HKDF-Expand( prk = VaultKey, info = "bastion/v1/index",   L = 32 )
    BlobKey   = HKDF-Expand( prk = VaultKey, info = "bastion/v1/blob" || blobId(16), L = 32 )

`vaultId` is never stored; it is derived after unwrap and used only inside AADs and
as the key of local per-machine records. It changes automatically on re-key.

### 2.5 Key wrap

    wrappedVaultKey = AES-256-GCM-Encrypt( key = KEK, nonce = wrapNonce, plaintext = VaultKey, aad = wrapAAD )
                    = 32 bytes ciphertext || 16 bytes tag

A failed unwrap (tag mismatch) is reported as `AuthenticationFailed`: "wrong password
or keyfile, or the vault header has been altered". These cases are deliberately
indistinguishable; no separate password verifier exists.

**MUST (writer):** every operation that produces a `wrappedVaultKey` generates a
fresh `kdfSalt` and a fresh `wrapNonce` from the CSPRNG. There is no code path that
reuses either. (Reusing both with a different VaultKey would be GCM nonce reuse.)

### 2.6 AAD construction

Let `H` be the 160 header bytes as they appear in the file.

    wrapAAD  = "bastion/v1/wrap"  || H with bytes [76, 156) set to zero
               (i.e. wrappedVaultKey, indexNonce, indexCopyNonce, indexLength zeroed)
    indexAAD = "bastion/v1/index" || H with bytes [124, 148) set to zero
               (i.e. both index nonces zeroed; indexLength and wrappedVaultKey ARE covered)

Labels are ASCII without terminator: `"bastion/v1/wrap"` is 15 bytes, `"bastion/v1/index"` is 16 bytes.

`wrapAAD` therefore binds: magic, formatVersion, headerLength, flags, kdfId,
cipherId, reserved fields, KDF parameters, kdfSalt, wrapNonce. It is stable across
ordinary saves, so the wrapped key can be copied verbatim.
`indexAAD` binds the entire header except the two nonces (which authenticate
themselves as the GCM nonce). The same `indexAAD` is used for the index and its copy.

### 2.7 Chunk encryption

A file's content is stored as one **blob**. A blob consists of
`chunkCount = ceil(length / chunkSize)` chunks, **minimum 1** (an empty file has one
empty chunk). Chunk `i` (0-based) holds plaintext bytes `[i*chunkSize, min((i+1)*chunkSize, length))`.

    nonce_i  = u32 i (little-endian) || 8 zero bytes                       (12 bytes)
    isLast   = (i == chunkCount - 1) ? 1 : 0
    aad_i    = "bastion/v1/chunk" || vaultId(16) || blobId(16) || u32 i || u8 isLast
    chunk_i  = AES-256-GCM-Encrypt( BlobKey, nonce_i, plaintext_i, aad_i ) = ciphertext_i || tag_i(16)
    blob     = chunk_0 || chunk_1 || ... || chunk_{chunkCount-1}
    blobLength = length + 16 * chunkCount

"bastion/v1/chunk" is 16 ASCII bytes. The reader derives `chunkCount`, every chunk's
length and `isLast` from the index; it never trusts framing inside the data section.

**Nonce safety invariant (MUST):** the pair `(BlobKey, i)` never encrypts two
different plaintexts. This holds by construction because:
1. `blobId` is 16 fresh CSPRNG bytes for **every content write** (import, copy,
   re-encrypt, re-key). A blob is never modified in place; changed content is a new blob.
2. `chunkCount <= 2^32 - 1` (MUST, see limits) so the counter never wraps.
3. Duplicate `blobId`s within one index are rejected (`IndexInvalid`).

GCM limits: per-invocation plaintext <= 64 MiB (< 2^39 - 256 bits) and at most
2^32 - 1 invocations per BlobKey — both satisfied by construction.

### 2.8 Blob commitment

    blobHash = SHA-256( blob )        (over the ciphertext bytes, all chunks)

Stored in the file entry. `Verify` recomputes and compares it. Because the index is
rewritten and re-authenticated on every save, `blobHash` commits the current index
to the exact bytes of each blob (freshness); the per-blob key already prevents
cross-version splicing, `blobHash` is defence in depth and makes "Verify OK" mean
"every byte is accounted for".

---------------------------------------------------------------------------
## 3. Header (160 bytes)

| off | size | field            | value / rule                                                  |
|-----|------|------------------|---------------------------------------------------------------|
| 0   | 8    | magic            | `89 42 53 54 4E 0D 0A 1A` (`\x89BSTN\r\n\x1A`) — else `NotAVault` |
| 8   | 2    | formatVersion    | 1 — greater: `UnsupportedVersion`; 0: `HeaderCorrupt`        |
| 10  | 2    | headerLength     | 160 for v1 — else `HeaderCorrupt`                             |
| 12  | 4    | flags            | bits 0..15 critical (unknown set bit: `UnsupportedParameters`), bits 16..31 advisory (ignored). v1 defines no bits; writers write 0 |
| 16  | 1    | kdfId            | 1 = Argon2id — else `UnsupportedParameters`                   |
| 17  | 1    | cipherId         | 1 = AES-256-GCM — else `UnsupportedParameters`                |
| 18  | 2    | reserved0        | 0 — else `HeaderCorrupt`                                      |
| 20  | 4    | kdfMemoryKiB     | see limits                                                    |
| 24  | 4    | kdfIterations    | see limits                                                    |
| 28  | 4    | kdfParallelism   | see limits                                                    |
| 32  | 32   | kdfSalt          | random                                                        |
| 64  | 12   | wrapNonce        | random                                                        |
| 76  | 48   | wrappedVaultKey  | §2.5                                                          |
| 124 | 12   | indexNonce       | random, fresh on every save                                   |
| 136 | 12   | indexCopyNonce   | random, fresh on every save, MUST differ from indexNonce      |
| 148 | 8    | indexLength      | ciphertext length incl. tag = paddedIndexLength + 16          |
| 156 | 4    | reserved1        | 0 — else `HeaderCorrupt`                                      |

There is **no** CRC and **no** plaintext vault identifier. The header contains no
information about the contents except the KDF cost and the padded index size.

### 3.1 Header validation order (MUST, all before any KDF work)

1. `fileLength >= 160` else `Truncated`; read 160 bytes.
2. magic → `NotAVault`.
3. formatVersion → `UnsupportedVersion` / `HeaderCorrupt`.
4. headerLength == 160, reserved0 == 0, reserved1 == 0 → `HeaderCorrupt`.
5. critical flag bits, kdfId, cipherId → `UnsupportedParameters`.
6. KDF parameter limits (§7) → `UnsupportedParameters`.
7. `indexLength` in `[16 + 65536, 64 MiB + 16]` and `fileLength >= 160 + 2*indexLength` → `Truncated` / `HeaderCorrupt`.
8. `indexNonce != indexCopyNonce` → `HeaderCorrupt`.
9. Resource pre-flight: if `kdfMemoryKiB * 1024 > 0.75 * installedPhysicalMemory` → `ResourceLimit`
   (message names the requirement and what the machine has). `installedPhysicalMemory` is the memory
   the machine physically has (`GlobalMemoryStatusEx.ullTotalPhys`, falling back to
   `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` where that call is unavailable). FREE memory is
   **not** consulted: it moves with whatever else the machine is doing this second, so measuring it
   refused the default 512 MiB preset on a 32 GiB machine during a busy moment. The pre-flight answers
   "could a machine this size ever serve this header", which has a stable answer; an allocation that
   fails anyway surfaces as the allocation failure it is.

Only then: Argon2id → unwrap → `AuthenticationFailed` on tag failure.

---------------------------------------------------------------------------
## 4. Index

### 4.1 Encryption

    indexCiphertext     = AES-256-GCM-Encrypt( IndexKey, indexNonce,     indexPlaintextPadded, indexAAD )
    indexCopyCiphertext = AES-256-GCM-Encrypt( IndexKey, indexCopyNonce, indexPlaintextPadded, indexAAD )

The reader tries the primary index first. If its tag fails it tries the copy; if
the copy authenticates, the vault opens and the session reports
`OpenedFromIndexCopy = true` ("save to repair"). If both fail: `IndexCorrupt`.

### 4.2 Padding ladder

`indexPlaintextPadded` is the serialized index followed by zero bytes up to
`PadLadder(unpaddedLength)`:

    PadLadder(n) = 65536                          if n <= 65536
                 = next power of two >= n         if n <= 1 MiB
                 = next multiple of 1 MiB >= n    otherwise

Padding bytes MUST be zero (`IndexInvalid` otherwise). `indexLength = PadLadder(n) + 16`.

### 4.3 Plaintext layout

```
u32  indexVersion         = 1                  (else IndexInvalid)
u32  unpaddedLength       total meaningful bytes incl. this field block (padding starts here)
u64  saveCounter          1 at creation, +1 on every successful save
i64  savedUtcTicks        DateTime.Ticks (UTC) of the save
u64  dataSectionLength    == sum(blobLength_i) + dataPaddingLength
u64  dataPaddingLength    trailing CSPRNG bytes at the end of the data section (0 unless size obfuscation)
u32  nextEntryId          next id to allocate; > every id in the index
u32  entryCount
entries[entryCount]       see 4.4, in canonical order (4.5)
-- zero padding to PadLadder(unpaddedLength) --
```

### 4.4 Entry

```
u8   kind          0 = folder, 1 = file            (else IndexInvalid)
u32  id            1 .. 0xFFFFFFFE, unique
u32  parentId      0 = root, or the id of a FOLDER entry that appeared EARLIER in the array
u16  nameLen       byte length of name (1 .. 765)
b*   name          strict UTF-8; validated per §6
i64  createdUtcTicks
i64  modifiedUtcTicks
u32  attributes    0 in v1 (else IndexInvalid)
u16  commentLen    0 .. 4096
b*   comment       strict UTF-8, no C0/C1 controls except TAB/LF/CR,
                   and none of the invisible formatting characters of §6.1
-- file entries only --
b16  blobId        unique within the index
u64  dataOffset    offset of the blob relative to the start of the data section
u64  length        plaintext length, <= 2^48 - 1
u32  chunkSize     power of two in [65536, 67108864]; writers use 1 MiB
b32  blobHash      SHA-256 of the blob ciphertext
```

Timestamps outside `[0, 3155378975999999999]` are clamped to 0 on read (never thrown).

### 4.5 Canonical order (MUST for writers; readers require the parent-first property)

Entries are serialized depth-first pre-order from the root, children of a folder
ordered by ascending `id`. This guarantees every `parentId` refers to an earlier
entry, so the tree is built in one iterative pass and cycles are unrepresentable.
Two conforming writers produce identical bytes for the same tree.

### 4.6 Index validity rules (MUST, all `IndexInvalid`)

Validation completes over the whole entry array before any node is exposed.
Everything is computed in checked arithmetic; the reader never allocates or slices
from a length field before verifying `remaining >= n`; collections are grown, never
pre-sized from `entryCount`.

1. `indexVersion == 1`; `unpaddedLength <= paddedLength`; bytes after `unpaddedLength` are zero; the entry array ends exactly at `unpaddedLength`.
2. `entryCount <= 1_000_000`.
3. `id` non-zero, `!= 0xFFFFFFFF`, unique; `id < nextEntryId`.
4. `parentId == 0` or refers to an earlier entry with `kind == folder`.
5. Depth (root = 0) `<= 128`.
6. `name` valid per §6; unique among siblings under `OrdinalIgnoreCase`.
7. `blobId` unique.
8. `chunkSize` a power of two in `[2^16, 2^26]`; `chunkCount = max(1, ceil(length / chunkSize)) <= 2^32 - 1`; `length <= 2^48 - 1`.
9. Blobs sorted by `dataOffset` tile `[0, dataSectionLength - dataPaddingLength)` exactly: first offset is 0, each next offset equals previous offset + previous `blobLength`, and the last blob ends exactly at `dataSectionLength - dataPaddingLength`. No gaps, no overlaps, no sharing (each blob referenced by exactly one entry).
10. `dataPaddingLength <= dataSectionLength`.
11. After index validation: the length equation of §1 holds exactly (`Truncated` otherwise).

---------------------------------------------------------------------------
## 5. Data section

Blobs in index order of `dataOffset`, then `dataPaddingLength` CSPRNG bytes. Padding
is unauthenticated by design (it is random and never interpreted; its *length* is
authenticated). Size obfuscation, when enabled by the user, pads
`sum(blobLength)` up to the `PadLadder`-style schedule
`ObfuscationLadder(n) = next multiple of max(1 MiB, 2^floor(log2 n) / 16)`; the
exact schedule is a writer choice and readers do not care.

---------------------------------------------------------------------------
## 6. Entry names and paths

### 6.1 Valid name (Layer 1, enforced on parse, on every mutation, and again on export)

A name is valid iff all hold:
- 1 .. 255 UTF-16 code units; strict UTF-8 on disk (invalid sequences → `IndexInvalid`).
- Contains none of `\ / : * ? " < > |`.
- Contains no C0/C1 control (U+0000–U+001F, U+007F–U+009F) and no invisible formatting
  character, defined as Unicode general category `Cf` (the bidi controls U+200E, U+200F,
  U+202A–U+202E, U+2066–U+2069 and U+061C; the BOM U+FEFF; the zero-width characters
  U+200B–U+200D; the soft hyphen U+00AD; the word joiner U+2060; and every other `Cf`),
  `Zl` (U+2028 LINE SEPARATOR) or `Zp` (U+2029 PARAGRAPH SEPARATOR). WPF and Explorer both
  break a line on `Zl`/`Zp` even with wrapping off, so such a name hides its own extension.
- Is not `.` or `..`, has no leading or trailing whitespace, no trailing `.`.
- Its stem (text before the first `.`, or the whole name) is not, case-insensitively,
  one of `CON PRN AUX NUL COM0–COM9 LPT0–LPT9`.
Names compare with `OrdinalIgnoreCase` for uniqueness among siblings.

### 6.2 Sanitising a disk name on import (deterministic)

1. NFC-normalise. 2. Remove control characters and every `Cf`/`Zl`/`Zp` character of
§6.1. 3. Trim leading/trailing whitespace and trailing dots. 4. Replace invalid
characters with `_`. 5. If the stem is a reserved device name, append `_` to the stem.
6. If empty, use `_`. 7. Truncate to 255 code units preserving the extension where
possible. 8. On sibling collision, apply Explorer's rule: `name (2).ext`,
`name (3).ext`, ... Every changed name is reported.

The uniquifier of step 8 is itself bound by §6.1: when the extension leaves no room for
a shortened stem it falls back to `(2).ext` with the extension trimmed, and the result
MUST pass validation. A mutation may never place a name in the tree that the index
serializer would refuse — that would block every later save.

### 6.3 Path grammar

In-vault paths are `\`-separated, root is `\`, no drive letter, resolved
case-insensitively: `\Documents\2026\notes.txt`. The leading separator is optional and
so is ONE trailing separator, so `\Docs`, `Docs` and `\Docs\` all address the same
folder; any further separator leaves an empty segment and the path is rejected. `Core`
provides `FormatPath` / `TryResolvePath`; the UI never concatenates path strings itself.

### 6.4 Export safety (Layer 2, defence in depth)

The destination path is built with `Path.Combine`, normalised with
`Path.GetFullPath`, and MUST start with the export root plus a separator
(`OrdinalIgnoreCase`). A destination that is itself a reparse point is refused, and the
string prefix is not enough on its own: EVERY existing directory component below the
export root MUST also be proved not to be a reparse point, because a junction on the way
redirects the whole path. A refused destination takes its whole subtree with it —
refusing only the leaf would let the descendants be written straight through the
junction, outside the export root. Files
are written with `FileMode.CreateNew` to a temporary sibling name and renamed on
success; exported files get `Zone.Identifier` `ZoneId=3` (Mark of the Web). Every
file-system call uses the `\\?\` (or `\\?\UNC\`) form of a fully qualified path longer
than 259 characters — `longPathAware` in the manifest only takes effect when the
machine-wide `LongPathsEnabled` registry opt-in is set as well, which is not the
default; the plain path stays the one shown in the report.

---------------------------------------------------------------------------
## 7. Limits table (normative; `VaultLimits` mirrors it)

| Quantity                    | Min        | Max                 | Violation code           |
|-----------------------------|------------|---------------------|--------------------------|
| kdfMemoryKiB                | 8192 (8 MiB) and >= 8*p | 4 194 304 (4 GiB); must be a multiple of 4*p | UnsupportedParameters |
| kdfIterations               | 1          | 64                  | UnsupportedParameters    |
| kdfParallelism              | 1          | 16                  | UnsupportedParameters    |
| KDF memory vs machine       | —          | 75 % of installed physical memory | ResourceLimit |
| password bytes              | 1          | 1024                | (argument error)         |
| keyfile bytes               | 1          | 1 MiB               | UnsupportedParameters    |
| indexLength                 | 65552      | 64 MiB + 16         | HeaderCorrupt            |
| index plaintext             | —          | 64 MiB              | IndexInvalid             |
| entryCount                  | 0          | 1 000 000           | IndexInvalid             |
| tree depth                  | —          | 128                 | IndexInvalid             |
| name                        | 1 code unit| 255 code units / 765 bytes | IndexInvalid / NameInvalid |
| comment                     | 0          | 4096 bytes          | IndexInvalid             |
| chunkSize                   | 64 KiB     | 64 MiB, power of 2  | IndexInvalid             |
| file length                 | 0          | 2^48 - 1            | IndexInvalid             |
| chunkCount                  | 1          | 2^32 - 1            | IndexInvalid             |
| Recommended vault size      | —          | low GiB range (whole-file rewrite on save) | (documentation) |

KDF presets (all `p = 4`): **Fast** 64 MiB / t=3 · **Standard (default)** 512 MiB / t=3 · **Strong** 1 GiB / t=4.
Measured on a 16-core desktop: 0.14 s / ~0.5 s / 1.1 s. The UI shows a measured
estimate for the current machine, not these numbers.

---------------------------------------------------------------------------
## 8. Writer behaviour

### 8.1 Creation
Generate VaultKey, kdfSalt, wrapNonce; derive KEK; wrap; build an empty index with
`saveCounter = 1`; write per §8.3.

### 8.2 Session model
The vault file stays open for the life of the session with
`FileShare.Read | FileShare.Delete`, reads use `RandomAccess` with explicit offsets.
Edits (create folder, rename, move, delete, import, copy, credential change) are
held in memory and applied by **Save**. Imported content is encrypted immediately
with its final BlobKey into staging (§8.5); plaintext never touches disk.

### 8.3 Save state machine (MUST)

```
 0. Preconditions: not read-only; free space on the vault volume >= estimatedNewLength + 64 MiB
    (DiskFull); if the file has the ReadOnly attribute → ReadOnlyTarget.
 1. Changed-on-disk check: re-stat the vault path via a FRESH handle; compare
    (length, LastWriteTimeUtc) with the values captured at open/last save → ChangedOnDisk.
 2. Build the complete index in memory (offsets known: unchanged blobs keep their
    length; staged blobs have known length). Serialize, pad, encrypt twice.
 3. Write <name>.bastion.tmp-<8 hex> in the SAME directory, sequentially, one pass:
    header → index → for each blob in offset order: copy verbatim (unchanged, same
    VaultKey) or stream from staging, or decrypt+re-encrypt (SaveMode.Rekey / copy)
    → data padding → index copy. Progress is reported; cancellation deletes the temp.
    The writer MUST refuse a verbatim copy when source and destination BlobKey differ
    (InvalidOperation) — this is what makes re-key correct by construction.
 4. Flush(true); close temp.
 5. Close the vault handle.
 6. Repeat step 1 (fresh stat). Then File.Replace(temp, vault, <name>.bastion.bak-<8 hex>,
    ignoreMetadataErrors: true). Retry up to 6 times with backoff 100·2^n ms (+ jitter)
    on IOException with Win32 code in {0x20, 0x21, 0x497, 0x498, 0x499}. If Replace
    A sharing violation that survives all 6 attempts is reported as Locked, naming the
    attempt count. If Replace is unsupported (ERROR_NOT_SAME_DEVICE / invalid parameter):
    File.Move(vault, bak), File.Move(temp, vault). Between those two moves the temp is the
    ONLY copy of the new vault, so it is taken out of the cleanup path first and a failure
    of the second move names both the temp and the .bak. Never copy over the original.
 7. Reopen the vault (FileShare.Read | Delete); capture the new (length, LastWriteTimeUtc).
 8. Post-save verification: parse header, decrypt index, compare entry set with what
    was written, decrypt the first and last chunk of up to 3 blobs. Failure → keep the
    .bak, report SaveVerificationFailed with both paths.
 9. Delete the .bak; drop staging; saveCounter += 1 in memory; undo stack cleared; not dirty.
    Step 9 runs even when step 7 could not reopen the file: the save is committed and
    verified, so the session's view MUST match what is on disk or the next save reports
    ChangedOnDisk against the very file it just wrote. The reopen failure is reported
    separately, as a normal VaultIoException.
On any failure after step 5 the session reopens the original path if it exists;
otherwise it reports the absolute path currently holding the user's data. A cancelled
or failed save never leaves a .tmp behind.

The save works on COPIES of the key material it needs, which it disposes itself: `Lock`
runs on any thread without the operation gate (§8.8), and a save that is already past
step 6 must still be able to finish and to verify what it wrote. A lock that lands during
a save therefore never turns a committed, correct file into a `SaveVerificationFailed`
verdict; step 9 still runs, and a re-key adopts the new vaultId while leaving the session
locked.
```

### 8.4 Save modes
- `Compact` (default): VaultKey unchanged; blobs copied verbatim by byte range.
- `Rekey`: VaultKey changed (password change, "Save as copy", "re-encrypt everything"):
  every blob (stored and staged) is streamed decrypt → encrypt under a fresh blobId.
  Blob lengths are unchanged, so offsets are computed identically.
- Credential changes: **Change password** defaults to `Rekey` (new VaultKey, new
  kdfSalt, new wrapNonce, new derived vaultId, new blobIds). "Fast change (rewrap only)"
  is an explicit option and keeps VaultKey. **Change KDF preset** and **add/remove
  keyfile** are rewrap-only (new kdfSalt + wrapNonce, same VaultKey). All of these are
  pending until the next Save, which is the single commit point.
- `Save As` / `Save a copy` always uses `Rekey` so two divergent copies never share a key space.

### 8.5 Staging
- Pending imports up to an aggregate 64 MiB are held in memory (ciphertext).
- Above that, all staged ciphertext goes into ONE append-only container
  `<name>.bastion~stage-<guid>` in the vault's directory (`FileShare.None`,
  `FileOptions.DeleteOnClose`, `FileAttributes.Temporary`), with an in-memory
  (blobId → offset, length) map. Fallback location `%LOCALAPPDATA%\BastionVault\staging`
  is used when the vault directory is not writable, its volume lacks room, or the
  vault sits under a cloud-sync root (OneDrive, Dropbox, iCloudDrive, Google Drive,
  or a reparse-tagged cloud folder). Users can override the location in settings.
- Pre-flight before an import: staged bytes + estimated new vault length must fit on
  the relevant volume(s) → `DiskFull` before any byte is read.
- Startup, lock and post-save sweeps remove `*~stage-*` and `*.tmp-*` orphans whose
  exclusive lock can be taken (a live session holds its container and its temporary
  file open). Neither pattern may assume the `.bastion` extension: the save temporary is
  named after whatever the vault file is called, so a vault named `archive.vault` leaves
  `archive.vault.tmp-<8 hex>` behind.
- The pre-flight compares against the in-memory budget only while staging is still in
  memory. Once it has spilled, every further byte goes to the container however small it
  is, so the staging volume is checked from then on regardless of size.
- Staged ciphertext is bound to the current VaultKey; `SaveMode.Rekey` re-encrypts it.

### 8.6 Import rules
- Enumerate with `AttributesToSkip = ReparsePoint`; never follow junctions or symlinks
  (skipped items appear in the import report). Depth is walked iteratively.
- Sources are opened with `FileShare.Read | Write | Delete`; `length` is the number
  of bytes actually read. After reading, the source is re-stat'ed; if length or mtime
  changed the entry is dropped and reported.
- Import is continue-on-error with a report (locked, unreadable, reparse point, renamed);
  cancel discards that import's staged blobs as a unit.

### 8.7 Export rules
Streamed decrypt to `<dest>.tmp-<8 hex>` then rename; a tag failure deletes the partial
output and is reported; export continues with the next file (continue-on-error +
report). Timestamps are restored. Empty folders are created. **Recover** is Export
with the additional opt-in to write the authenticated prefix of a damaged file as
`name.partial`.

### 8.8 Lock
`Lock` zeroes VaultKey, IndexKey, all BlobKeys and pending credential material and
discards any pending credential change. The in-memory tree and staged ciphertext are
kept so unsaved work survives; `Unlock` re-derives the KEK, unwraps VaultKey from the
header and MUST verify that the derived vaultId equals the session's vaultId.

---------------------------------------------------------------------------
## 9. Error codes (`VaultErrorCode`)

`NotAVault`, `UnsupportedVersion`, `UnsupportedParameters`, `HeaderCorrupt`,
`Truncated`, `AuthenticationFailed`, `IndexCorrupt`, `IndexInvalid`, `DataCorrupt`
(carries entry path + chunk index), `ResourceLimit`, `DiskFull`, `ReadOnlyTarget`,
`Locked` (sharing violation after retries; carries path), `ChangedOnDisk`,
`SaveVerificationFailed`, `IoError`, `NameInvalid`, `NameConflict`, `InvalidMove`,
`Busy`, `SessionLocked`, `ReadOnlySession`, `Cancelled`.

`AuthenticationFailed` is one bucket by design (wrong password / wrong or missing
keyfile / altered header). Header failures are reported **before** the KDF runs;
`IndexCorrupt` after a successful unwrap means "your password is correct but the
vault has been altered or damaged — do not save over it".

---------------------------------------------------------------------------
## 10. Test vectors and fixtures

- Argon2id: RFC 9106 §5.3 (password 32×0x01, salt 16×0x02, secret 8×0x03, ad 12×0x04,
  m=32 KiB, t=3, p=4, tag 32) → `0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659`.
  Also §5.1 (Argon2d) and §5.2 (Argon2i) vectors for the shared core.
- `tests/fixtures/golden-v1-empty.bastion` and `tests/fixtures/golden-v1-small.bastion`
  are produced with `DeterministicRandomSource(seed: 0)` and a fixed clock
  (2026-01-01T00:00:00Z); tests regenerate and compare byte-for-byte. Regenerate only
  deliberately, by running the suite with the environment variable `BASTION_REGEN_GOLDEN=1`
  (VSTest does not forward `dotnet test -- …` arguments to xUnit v2; see
  `tests/fixtures/README.md`).
- The tamper matrix (one test per row) covers: every header field flipped; index
  ciphertext and tag; index copy; each chunk's ciphertext and tag; truncation at every
  structural boundary; appended bytes; swapped chunks within a blob; swapped blobs;
  blob spliced from another vault; blob replayed from an earlier save of the same
  vault after its content changed; old index on new header; duplicate ids / blobIds;
  parent cycles; non-tiling data sections; every numeric at 0, 1, max, max+1;
  depth 129; names in every invalid class.

---------------------------------------------------------------------------
## 11. Version history

- v1 (2026-09): initial format.
