# Security policy

Bastion Vault is a cryptographic tool. Reports about its security are welcome and taken
seriously. Please read this page before reporting.

## Status of the cryptography

Bastion Vault's format and implementation were designed and reviewed carefully, and are tested
against the RFC vectors, a tamper matrix and fuzzing (see `docs/FORMAT.md` and
`docs/THREAT-MODEL.md`). **They have not been independently audited by a third party.**
Argon2id and BLAKE2b are own implementations. Treat Bastion Vault accordingly: suitable for
personal use, not a substitute for an audited product where lives or livelihoods depend
on it.

## Supported versions

| Version | Supported |
|---|---|
| 1.x (format version 1) | yes |

Vault files written by any 1.x release open in every later 1.x release.

## Reporting a vulnerability

1. **Do not open a public issue for a security problem.**
2. Use GitHub's private vulnerability reporting on this repository
   (*Security → Report a vulnerability*). This keeps the report between you and the
   maintainer until a fix is available.
3. If private reporting is not available to you, open a public issue that says only
   "security contact requested" with no details; the maintainer will reply with a channel.

Please include: the Bastion Vault version, the exact steps or a crafted file that shows the
problem, what an attacker gains, and whether you have shared the finding elsewhere.

## What to expect

- Acknowledgement within 7 days.
- An assessment and a planned fix date within 30 days for confirmed issues.
- Target of 90 days from report to public fix. Coordinated disclosure after the fix is
  released; you will be credited unless you prefer not to be.

## In scope

- The vault format (`docs/FORMAT.md`): any way to read plaintext, learn metadata beyond
  what `docs/THREAT-MODEL.md` concedes, or alter a vault without detection.
- The cryptographic implementation (`src/BastionVault.Core/Crypto`), key handling and zeroing.
- The reader's robustness against crafted vaults (crashes, hangs, unbounded allocation,
  path traversal on export).
- Plaintext or metadata left on disk by the application.

## Out of scope

Anything listed under "Explicitly out of scope" in `docs/THREAT-MODEL.md`: malware on the
machine while a vault is open, secure deletion on SSDs, side channels beyond constant-time
tag comparison, deniability, and concurrent editing from two machines.
