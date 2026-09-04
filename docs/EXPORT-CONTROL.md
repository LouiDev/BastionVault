# Export-control self-classification (Selbsteinstufung Exportkontrolle)

**Item:** Bastion Vault, encrypted archive editor for Windows, source code and release binaries
published at https://github.com/LouiDev/BastionVault.
**Exporter / publisher:** LouiDev, Germany (customs territory of the European Union).
**Date of assessment:** 2026-09-04, at the moment the repository was made public.
**Reviewed:** on every change to the cryptography, the licence, or the way the software is
distributed. Record the date and outcome below under *History*.

This is a self-classification recorded for transparency and for any later inquiry. It is not
legal advice and not a ruling by an authority. A binding classification can be obtained free
of charge from the German Federal Office for Economic Affairs and Export Control (BAFA) as an
*Auskunft zur Güterliste*.

## 1. Legal basis

- Regulation (EU) 2021/821 of 20 May 2021 setting up a Union regime for the control of
  exports, brokering, technical assistance, transit and transfer of dual-use items
  (the EU Dual-Use Regulation), directly applicable in all member states, with its Annex I
  (the control list, derived from the Wassenaar Arrangement lists).
- In Germany: Außenwirtschaftsgesetz (AWG) and Außenwirtschaftsverordnung (AWV); competent
  authority: Bundesamt für Wirtschaft und Ausfuhrkontrolle (BAFA).

## 2. Technical description relevant to classification

Bastion Vault encrypts user files into a single container file (`.bastion`). The
cryptographic functions it employs (specified in `FORMAT.md`):

| Function | Algorithm | Parameters |
|---|---|---|
| Confidentiality and integrity of contents | AES-256-GCM (symmetric, authenticated) | 256-bit keys, 96-bit nonces, 128-bit tags |
| Password-based key derivation | Argon2id (RFC 9106) | 64 MiB to 1 GiB memory, 3 to 4 passes |
| Key expansion | HKDF-SHA256 (RFC 5869) | 256-bit outputs |
| Keyfile digest, blob commitment | HMAC-SHA256, SHA-256 | — |

The confidentiality function uses a symmetric algorithm with a key length above 56 bits.
Encryption is the primary function of the software, not an ancillary one. AES-GCM is taken
from the .NET runtime; Argon2 and BLAKE2b are implemented in this repository. The origin of
the primitives does not affect the classification of the software that employs them.

## 3. Classification

- **Annex I entry:** the software has the characteristics of an item specified in
  **5A002.a** (information-security systems employing cryptography for confidentiality with a
  symmetric key length exceeding 56 bits) and is therefore, in principle, **5D002.a.1**
  software (Category 5, Part 2, "Information Security").
- **Decontrol notes examined:**
  - *Note 3 to Category 5 Part 2 (Cryptography Note, mass-market items):* potentially
    applicable to freely downloadable release builds, but not relied upon.
  - *Note 4 (cryptography ancillary to primary function):* not applicable; encryption is the
    primary function.
  - *Decontrols for authentication-only, digital signature, copy protection:* not applicable.
- **General Software Note, item b — "in the public domain":** **applicable and relied upon.**
  Annex I defines *"in the public domain"* as technology or software "which has been made
  available without restrictions upon its further dissemination" and adds that "copyright
  restrictions do not remove 'technology' or 'software' from being 'in the public domain'".
  The General Software Note's retail item (a) is expressly withheld from Category 5 Part 2,
  but item (b) is not, so it applies to 5D002 software.

**Conclusion:** From the moment of publication, the complete source code of Bastion Vault is
made available to the public without any restriction on its further dissemination, in a public
repository that anyone can read, clone and redistribute. The software is therefore *in the
public domain* in the sense of Annex I and **is not controlled**. No export authorisation is
required for making it available or for downloads to any destination. The release binaries
are built from that public source by a public workflow and are downloadable without
restriction; they share the same status.

## 4. Conditions that keep this conclusion valid

1. **The repository stays public**, including the full source of every released version.
2. **The licence permits redistribution.** The PolyForm Noncommercial License 1.0.0 restricts
   the *purpose* of use (non-commercial only), which is a copyright restriction and does not
   affect public-domain status under Annex I. It does permit distribution of the software and
   of modified versions. A licence that forbade redistribution would put this conclusion in
   question and must not be adopted without a new assessment.
3. **Everything cryptographic is published.** No private branches, pre-release builds or
   closed add-on modules containing cryptographic functions are transmitted to persons outside
   the EU before they are public. Development happens in the open, so each change becomes
   public with the push that contains it.
4. **Binaries are reproducible from the public source** (see `DEVELOPING.md` and the CI
   workflow) and offered without access restrictions.

## 5. What this classification does not cover

- **Sanctions law** (EU restrictive measures, e.g. against Russia, Belarus, Iran, North
  Korea) is separate from the control list. An anonymous download from a public repository is
  not a targeted provision of resources. Knowingly supplying support, commercial licences,
  custom builds or technical assistance to a listed person, entity or embargoed destination is
  prohibited regardless of this classification. Commercial-licence requests are checked against
  the EU consolidated sanctions list before anything is delivered.
- **Catch-all controls** (Article 4 of the Regulation): even non-listed items require an
  authorisation if the exporter knows, or is informed by the authority, that they are intended
  for weapons of mass destruction or a military end use in an arms-embargoed destination.
- **Cyber-surveillance items** (Article 5): not relevant; the software protects the user's
  own data and has no surveillance capability.
- **The period before publication.** While the repository was private, its content was
  controlled 5D002 software. Storing it on a hosting provider outside the EU may be viewed as
  an export to that provider's country; for the United States such an export is covered by the
  Union General Export Authorisation EU001, which requires registration with BAFA within
  30 days of first use. This is recorded here for completeness; publication ended the
  controlled status.
- **Future commercial distribution.** A commercially licensed build with support or with
  proprietary, unpublished components would need its own assessment (most likely under the
  Cryptography Note for mass-market items). The published source remains uncontrolled.
- **United States rules.** The hosting provider is a US company. The US notification
  requirement for publicly available encryption source code (EAR §§ 740.13(e), 742.15(b))
  applies to the US person publishing, not to a publisher in Germany. Third-party components
  used by Bastion Vault (.NET, CommunityToolkit.Mvvm) are themselves publicly available
  open-source software.

## 6. History

| Date | Event | Outcome |
|---|---|---|
| 2026-09-04 | Repository made public under PolyForm Noncommercial 1.0.0; first release 1.0.0 (format version 1) | 5D002.a.1, decontrolled as "in the public domain" (General Software Note b) |
