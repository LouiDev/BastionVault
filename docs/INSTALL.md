# Installing Bastion Vault

Bastion Vault has no setup program. You download a zip file, unpack it into a folder, and
start the program from there. It does not need administrator rights and it does not change
anything on your computer that you did not ask for. This guide takes you through it step by
step. The technical details are collected at the very end, for those who want them.

## What you need

- A PC with **Windows 10 or Windows 11**, 64-bit. Almost every PC sold in the last ten years
  qualifies. (Windows on ARM tablets and laptops are not supported.)
- About five minutes.

## Step 1: Download

1. Open the [Releases page](https://github.com/LouiDev/BastionVault/releases).
2. The newest version is at the top. Under **Assets** you see several files. You need
   **one** of the two zip files:

   | Download this | If |
   |---|---|
   | `BastionVault-...-win-x64-selfcontained.zip` | You are not sure. This is the safe choice: everything the program needs is inside. The download is larger. |
   | `BastionVault-...-win-x64.zip` | You know that the ".NET 10 Desktop Runtime" is already on your PC, or you do not mind installing it. The download is much smaller. |

   If you pick the smaller one and the runtime is missing, the program tells you so when you
   start it and offers a link to download it from Microsoft. Choose ".NET Desktop Runtime",
   64-bit (x64), install it, and start Bastion Vault again.

3. Click the file name. Your browser saves it to your *Downloads* folder.

## Step 2: Unpack

1. Decide where the program should live. A good place is a new folder called
   `Bastion Vault` inside your *Documents* folder, or anywhere else you keep programs. Pick a
   place you will not move or rename later (Step 5 explains why).
2. Open your *Downloads* folder, right-click the zip file and choose **Extract All...**.
3. In the window that opens, click **Browse...**, choose the folder from point 1, and click
   **Extract**.

The folder now contains `BastionVault.exe` (the program), a few text files with the license,
and two files ending in `.pdb`. You can ignore or delete the `.pdb` files; they only help
developers.

## Step 3: Start it for the first time

Double-click `BastionVault.exe`.

**Windows will show a blue box saying "Windows protected your PC".** This is expected and not
a sign that something is wrong. Windows shows this box for every program that is not signed
with a paid publisher certificate, and Bastion Vault is not. To continue:

1. Click **More info** (small text under the message).
2. Click **Run anyway**.

Windows asks this only once per downloaded file. The next start is silent.

You now see the start screen with **Create vault** and **Open vault**. A *vault* is a single
file, ending in `.bastion`, that holds your encrypted files and folders. Create one, choose a
password you will remember, and you are ready.

## Step 4: Make it easy to find (optional)

The zip cannot add a Start menu entry for you. If you want one:

- Right-click `BastionVault.exe` and choose **Pin to Start**, or **Pin to taskbar**.

## Step 5: Open vaults by double-click (optional)

Out of the box, double-clicking a `.bastion` file does nothing, because Bastion Vault never
registers itself on your PC without asking. If you want double-click to work:

1. In Bastion Vault, open **Settings** (press `Ctrl` and `,` together, or use the
   *Settings* entry in the menu).
2. Find **Register the .bastion file type** and click **Register**.

From now on, double-clicking a vault opens it in Bastion Vault. The same button, now
labelled **Unregister**, switches it off again. This affects only your own Windows user
account.

**Important: after registering, do not move or rename the program folder.** Windows
remembers where the program was when you clicked *Register*. If you move it, double-click
stops working with no message. If you need to move it anyway: click *Unregister*, move the
folder, start the program from the new place, click *Register*.

## Updating to a new version

1. Close Bastion Vault.
2. Download the new zip (Step 1) and extract it **into the same folder** as before, replacing
   the old files when Windows asks.
3. Start it. The blue "Windows protected your PC" box appears once more because the file is
   new; click *More info*, then *Run anyway*.

Your settings and your list of recent vaults are kept. Every version 1.x opens every vault
that an earlier 1.x version created.

## Removing Bastion Vault

1. If you used Step 5, open *Settings* and click **Unregister** first, while the program is
   still there.
2. Close Bastion Vault and delete the program folder.
3. Delete the folder where the program keeps its settings: press `Windows` + `R`, type
   `%LOCALAPPDATA%\BastionVault`, press Enter, and delete everything in the folder that opens
   (or the folder itself).

**Your vaults are not touched by any of this.** They are ordinary files where you saved them.
Keep them, or delete them yourself if you no longer want them.

## Four things to know before you rely on it

- **There is no "forgot password".** Nobody, not even the author, can open a vault without
  its password (and its keyfile, if you added one). Write the password down and keep it
  somewhere safe.
- **A vault is one file. Back it up like any other file.** Copy it to an external drive or a
  second place now and then. Bastion Vault is not a backup tool.
- **Download only from the Releases page linked above.** The program is not signed, so a
  copy from anywhere else could have been altered. If you want to double-check a download,
  see *Verifying a download* below.
- **The encryption has not been independently audited.** It was designed carefully and is
  tested extensively, but no outside expert has reviewed it yet. What it does and does not
  protect against is spelled out in [THREAT-MODEL.md](THREAT-MODEL.md).

---

## For the technically minded

**Verifying a download.** Each release ships `SHA256SUMS.txt`. Download it next to the zip,
open a terminal in that folder (right-click the folder background, *Open in Terminal*) and
run:

```powershell
Get-FileHash .\BastionVault-v1.0.1-win-x64-selfcontained.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

The hash printed by the first command must match the line for that file name in the second.
If it does not, delete the download and get it again.

**What the program writes.** Everything is per user; nothing here ever contains vault
content, passwords or keys.

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\BastionVault\settings.json` | Settings (theme, auto-lock, default KDF preset, list columns, window placement). Plain JSON. |
| `%LOCALAPPDATA%\BastionVault\recent.dat` | Recent-vaults list (paths only), DPAPI-encrypted for your account. |
| `%LOCALAPPDATA%\BastionVault\rollback.dat` | Highest save counter seen per vault, so a vault file replaced by an older copy is detected. DPAPI-encrypted. |
| `%LOCALAPPDATA%\BastionVault\logs\` | Rolling text log; never contains entry names, in-vault paths, keys or salts. |
| `HKCU\Software\Classes\.bastion`, `HKCU\Software\Classes\BastionVault.Vault.1` | The file type from Step 5, only if you registered it. |
| `%TEMP%\.net\BastionVault\` | Self-contained variant only: the .NET runtime's native libraries, unpacked on first start. No user data. |

The program never connects to the network: no update check, no telemetry, no crash reports.

**If you deleted the program before unregistering the file type**, remove the two registry
keys by hand (current user only, no administrator rights needed):

```
reg delete HKCU\Software\Classes\.bastion /f
reg delete HKCU\Software\Classes\BastionVault.Vault.1 /f
```

**Command line.**

```
BastionVault.exe                        start screen
BastionVault.exe C:\path\to\my.bastion  open that vault
BastionVault.exe --demo                 in-memory demo vault; any password unlocks it
```

One process per vault: opening a vault that is already open brings its window to the front
instead of opening the file twice.

**Runtime.** The framework-dependent zip needs the .NET 10 Desktop Runtime, x64:
`winget install Microsoft.DotNet.DesktopRuntime.10`, or download it from
<https://dotnet.microsoft.com/download/dotnet/10.0>. The plain ".NET Runtime" and the
"ASP.NET Core Runtime" do not include WPF and are not enough.

**Building from source** is described in [README.md](../README.md).
