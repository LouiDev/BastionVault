# Installing Bastion Vault

Bastion Vault is distributed as a zip archive, not as an installer. The program is a single
executable that runs from any folder, needs no administrator rights, and writes only to your
own user profile. This page explains which download to pick, how to verify it, what to expect
on first start, what the program leaves on your machine, and how to remove it completely.

For building from source see the *Build from source* section of [README.md](../README.md).

## 1. Requirements

- Windows 10 or Windows 11, 64-bit (x64). There is no ARM64 or 32-bit build.
- For the `framework-dependent` variant: the **.NET 10 Desktop Runtime (x64)**. The
  `selfcontained` variant needs nothing else.
- Enough free memory for the vault's key derivation. The default preset uses 512 MiB while a
  vault is being unlocked; the unlock screen shows the exact figure for each vault.

## 2. Choosing a variant

Every release on the [Releases page](https://github.com/LouiDev/BastionVault/releases)
carries two zips and a checksum file:

| File | What it is | Pick it when |
|---|---|---|
| `BastionVault-vX.Y.Z-win-x64.zip` | Framework-dependent. Small download; uses the .NET 10 Desktop Runtime installed on the machine. | You already have the runtime, or you keep several .NET programs and want Windows Update / winget to service the runtime centrally. |
| `BastionVault-vX.Y.Z-win-x64-selfcontained.zip` | Self-contained. Larger download; the runtime is inside the executable. | You want one file that works on a machine you do not control, or you do not want to install a runtime. |
| `SHA256SUMS.txt` | SHA-256 of both zips. | Always, to verify the download (section 3). |

If you pick the framework-dependent variant and the runtime is missing, the first start shows a
Windows dialog with a download link. You can also install it ahead of time:

```
winget install Microsoft.DotNet.DesktopRuntime.10
```

or download ".NET Desktop Runtime 10.0, Windows x64" from
<https://dotnet.microsoft.com/download/dotnet/10.0>. Only the *Desktop* runtime works; the
plain ".NET Runtime" and the "ASP.NET Core Runtime" do not include WPF.

## 3. Download and verify

The executables are **not code-signed** (see section 9), so the checksum is the only thing
that ties the file you downloaded to the file the CI built. Download the zip and
`SHA256SUMS.txt` into the same folder, then in PowerShell:

```powershell
Get-FileHash .\BastionVault-vX.Y.Z-win-x64-selfcontained.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

The hash printed by `Get-FileHash` must match the line for that file name in `SHA256SUMS.txt`
exactly. If it does not, delete the download and get it again from the Releases page; do not
run it.

## 4. Unpack and first start

1. Pick a permanent folder, for example `C:\Program Files\BastionVault` (needs one
   administrator confirmation when copying) or `%LOCALAPPDATA%\Programs\BastionVault` (no
   confirmation). Choose the place you will keep it: if you register the `.bastion` file type
   later (section 5), that registration stores this path.
2. Right-click the zip, *Extract All...*, and point it at that folder. The zip contains
   `BastionVault.exe`, two debug-symbol files (`*.pdb`, safe to delete), and `LICENSE`,
   `NOTICE`, `THIRD-PARTY-NOTICES.md`, `README.md`.
3. Start `BastionVault.exe`. Because the file is not signed, **Windows SmartScreen shows
   "Windows protected your PC"** the first time. Click *More info*, then *Run anyway*. This
   happens once per downloaded file; after that Windows remembers the decision.
4. The start screen offers *Create vault* and *Open vault*. To look around without a real
   vault, start the program with `--demo` (section 8).

Optional: create a Start menu or taskbar shortcut by right-clicking `BastionVault.exe` and
choosing *Pin to Start* or *Pin to taskbar*. The zip does not do this for you.

## 5. Registering the `.bastion` file type (optional)

By default double-clicking a `.bastion` file does nothing; Bastion Vault never registers
itself behind your back. To make Explorer open vaults with Bastion Vault:

1. In the program, open *Settings* (`Ctrl+,` or the *Settings* menu entry).
2. Under *Register the .bastion file type*, click **Register**.

The registration is written under your own user only
(`HKEY_CURRENT_USER\Software\Classes\.bastion` and
`HKEY_CURRENT_USER\Software\Classes\BastionVault.Vault.1`); other accounts on the machine are
not affected and no administrator rights are needed. The same button, now labelled
**Unregister**, removes it again.

**Do not move or rename the program folder after registering.** The registration points at
the executable's full path; if the file is no longer there, double-clicking a vault fails
silently. If you must move it: *Unregister*, move, start the program from the new place,
*Register*.

## 6. What Bastion Vault writes to your machine

Everything below lives in your user profile. **No vault content, no passwords and no keys are
ever written to any of these places.** The vault is only ever the `.bastion` file you created,
wherever you chose to put it.

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\BastionVault\settings.json` | Program settings: theme, auto-lock timer, default KDF preset, list columns, window placement. Plain JSON. |
| `%LOCALAPPDATA%\BastionVault\recent.dat` | The recent-vaults list (file paths only), encrypted with Windows DPAPI for your account. |
| `%LOCALAPPDATA%\BastionVault\rollback.dat` | The highest save counter seen per vault, so a vault file that was silently replaced by an older copy is detected. DPAPI-encrypted. |
| `%LOCALAPPDATA%\BastionVault\logs\` | Rolling text log. Never contains entry names, in-vault paths, keys or salts. |
| `HKCU\Software\Classes\.bastion`, `HKCU\Software\Classes\BastionVault.Vault.1` | Only if you registered the file type (section 5). |
| `%TEMP%\.net\BastionVault\` | `selfcontained` variant only: the .NET runtime's native libraries, unpacked from the single executable on first start. No user data. |

Bastion Vault never connects to the network. There is no update check, no telemetry and no
crash reporting.

## 7. Updating

1. Download and verify the new zip (section 3).
2. Close Bastion Vault.
3. Extract the new zip over the existing folder, replacing `BastionVault.exe` and the other
   files in place. Keeping the same folder keeps a registered file type (section 5) working.
4. Start it; SmartScreen will ask once more because the file changed.

Settings, the recent list and the rollback record are kept. Every 1.x release reads and writes
format version 1, so an updated program opens every vault the previous one created. Read the
[CHANGELOG](../CHANGELOG.md) before updating; anything that affects existing vaults is listed
there.

## 8. Command line

```
BastionVault.exe                       start screen
BastionVault.exe C:\path\to\my.bastion open (unlock) that vault
BastionVault.exe --demo                in-memory demo vault; any password unlocks it
```

Bastion Vault runs one process per vault. Opening a vault that is already open in another
window brings that window to the front instead of opening the file a second time.

## 9. Before you trust it with data

- The executables are **not code-signed**. Verify the checksum (section 3) and download only
  from the project's Releases page.
- The cryptography has **not been independently audited**. Read
  [THREAT-MODEL.md](THREAT-MODEL.md) for what is and is not protected, and
  [SECURITY.md](../SECURITY.md) for how to report a problem.
- **There is no password recovery.** If you lose the password (or the keyfile, if you use one),
  the vault's content is gone.
- **A vault is one file. Back it up like any other file.** Bastion Vault is not a backup tool.

## 10. Uninstalling completely

1. If you registered the file type, open *Settings* and click **Unregister** first (the
   button needs the executable to still exist).
2. Close Bastion Vault and delete the program folder.
3. Delete `%LOCALAPPDATA%\BastionVault` (settings, recent list, rollback record, logs).
4. `selfcontained` variant only: delete `%TEMP%\.net\BastionVault` if it exists.
5. Your `.bastion` files are untouched by all of the above. Delete them yourself if you want
   them gone; there is nothing else to clean up.

If you deleted the program folder before unregistering, remove the two registry keys by hand
(current user only, no administrator rights needed):

```
reg delete HKCU\Software\Classes\.bastion /f
reg delete HKCU\Software\Classes\BastionVault.Vault.1 /f
```
