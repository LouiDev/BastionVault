# Developing Bastion Vault

How to build it, test it, run it, and where everything lives. The normative documents are
`API.md` (the frozen `BastionVault.Core` surface), `UI-CONTRACT.md` (the App's rules and the
"Lamplight" design language), `FORMAT.md` (the on-disk format) and `THREAT-MODEL.md`.
This file is the working manual; it decides nothing.

---------------------------------------------------------------------------
## 1. Prerequisites

- **.NET SDK 10.0** (the repo builds with 10.0.400).
- **Windows 10 1809 or later**, x64. `BastionVault.App` is `net10.0-windows` with `UseWPF`;
  only `BastionVault.Core` (`net10.0`) is portable, and only it has tests that would run
  anywhere.
- No other tooling. `Directory.Packages.props` pins every package version centrally;
  there are exactly three: CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection
  and the test stack (xUnit, NSubstitute).

---------------------------------------------------------------------------
## 2. Build and test

```
dotnet build BastionVault.slnx
dotnet test  BastionVault.slnx
```

The tree is expected to build with **zero warnings**; treat a new one as a build break.
A full run is about 15 seconds:

| Project              | Tests | Covers                                                    |
|----------------------|-------|-----------------------------------------------------------|
| `BastionVault.Core.Tests` |  747  | crypto vectors, the format, the session, the tamper matrix, golden fixtures |
| `BastionVault.App.Tests`  |  309  | view models, converters, the keymap, the video thumbnailer, and real end-to-end runs |

Useful filters:

```
dotnet test tests/BastionVault.Core.Tests --filter "FullyQualifiedName~Vault.BlobTamper"
dotnet test tests/BastionVault.App.Tests  --filter "FullyQualifiedName~EndToEnd"
```

### The end-to-end test

`tests/BastionVault.App.Tests/EndToEnd/RealVaultEndToEndTests.cs` is the one test that proves the
parts fit together: the real `VaultFactory`, the real `ShellViewModel` and
`ExplorerViewModel`, a real file in the temp directory, and only the dialogs and OS pickers
substituted with NSubstitute. It creates a vault, imports a folder of three files, saves,
locks, unlocks, renames, undoes, exports and compares the bytes, verifies, re-keys the
password, saves, and reopens from disk with the new password — then asserts that no `.tmp-`,
`.bak-` or `~stage-` file survived. It uses `KdfParameters(8192, 1, 1)` so Argon2id costs
milliseconds. If it fails, fix it before anything else: every unit test can be green while
the product is broken.

### Regenerating the golden fixtures

`tests/fixtures/golden-v1-empty.bastion` and `golden-v1-small.bastion` are rebuilt from
scratch on every run and compared **byte for byte**. To rewrite them on purpose:

```
BASTION_REGEN_GOLDEN=1 dotnet test tests/BastionVault.Core.Tests          # bash
$env:BASTION_REGEN_GOLDEN='1'; dotnet test tests/BastionVault.Core.Tests  # PowerShell
```

`dotnet test -- --regenerate-golden` does **not** work: VSTest does not forward arguments
after `--` to xUnit v2. Never regenerate a fixture to turn a red test green — a difference
means either the format changed deliberately (and `FORMAT.md` says so) or a writer that must
be deterministic no longer is, which is a real bug. `tests/fixtures/README.md` lists exactly
what is pinned in each fixture.

### The video fixture

`tests/BastionVault.App.Tests/Fixtures/tiny-h264.mp4` is a 4 KB, two-second, 160 x 90 H.264 clip
(ffmpeg's `testsrc` pattern, `moov` box at the end of the file so a probe has to seek), and
`tiny-h264-frames.png` holds its eight frames as ffmpeg decoded them, stacked vertically.
`MediaFoundationThumbnailerTests` compares the frame Media Foundation returns against that strip,
which catches a bottom-up copy, a swapped channel order and a wrong stride at once. Both files
were made with:

```
ffmpeg -f lavfi -i testsrc=size=160x90:rate=4 -t 2 -pix_fmt yuv420p -c:v libx264 -profile:v baseline -preset veryslow -crf 30 tiny-h264.mp4
ffmpeg -i tiny-h264.mp4 -vf tile=1x8 -frames:v 1 tiny-h264-frames.png
```

The decode tests return early on a machine without Media Foundation (Windows N without the
Media Feature Pack); `MediaFoundationThumbnailer.IsAvailable` tells which case a run was.

---------------------------------------------------------------------------
## 3. Running the app

```
dotnet run --project src/BastionVault.App                          # start screen
dotnet run --project src/BastionVault.App -- C:\path\to\my.bastion  # open a vault at start-up
```

The built executable is `src/BastionVault.App/bin/Debug/net10.0-windows/BastionVault.exe`
(the assembly is named `BastionVault`, not `BastionVault.App`).

### Demo mode

```
dotnet run --project src/BastionVault.App -- --demo
dotnet run --project src/BastionVault.App -- --demo C:\vaults\demo.bastion
```

`--demo` swaps `BastionVault.Core`'s factory for an in-memory `FakeVaultSession`
(`Services/Demo/`), so every screen can be reached without a real vault: any password
unlocks, and the fake save takes about three seconds on purpose so the progress card, its
ETA and the non-cancellable tail are all visible. Pass a path as well to land on the unlock
card instead of the start screen. Demo mode holds no key material, which is why the
view-model boundary accepts a nullable `Passphrase`; the real path never passes null.

### Test hooks (Debug builds only)

The hooks below are compiled out of Release builds (`#if DEBUG` in `App.xaml.cs`), so a
shipped executable ignores the flags entirely. Use a Debug build for the UI-automation
recipe in section 4.

| Argument | Effect |
|----------|--------|
| `--test-pick-vault-create=<path>` | the New-vault Save picker answers this instead of opening |
| `--test-pick-vault-open=<path>` | likewise for Open vault |
| `--test-pick-import-folder=<dir>` | likewise for Import folder |
| `--test-pick-import-files=<a;b;c>` | likewise for Import files (semicolon separated) |
| `--test-pick-export-folder=<dir>` | likewise for the export destination |
| `--test-pick-keyfile=<path>`, `--test-pick-keyfile-create=<path>` | likewise for keyfiles |
| `--trace-bindings=<file>` | routes WPF's binding, resource, markup and dependency-property traces at Warning level into a text file |
| `--test-installed-memory=<bytes>` | the KDF pre-flight shown by the unlock card and the preset pickers pretends the machine has this much memory (for example `1073741824` makes the Strong preset "exceed this PC"); Core's real pre-flight and the real open are untouched |
| `--test-crash` | throws on the dispatcher once the window is up, so the crash window (`Shell/CrashWindow.cs`) can be seen |

The pickers exist because the Windows common file dialogs are separate windows whose
automation tree differs between Windows builds, so a UI-automation run cannot drive them
reliably. `ScriptedFileDialogService` only answers the pickers that were named; anything
else falls through to the real `FileDialogService`, so a mistyped flag shows an OS dialog
rather than silently cancelling. Every scripted answer is written to the log.

`--trace-bindings` writes one header line immediately, so an otherwise-empty file is
provably "no warnings" rather than "the listener never attached".

---------------------------------------------------------------------------
## 4. Screenshot and UI-automation workflow

The scripts used for the integration pass live in the scratchpad, not the repo, but the
recipe is worth keeping:

1. **Turn off capture exclusion.** `ExcludeFromScreenCapture` defaults to `true` and calls
   `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, so a screen capture records whatever
   is *behind* the window. Set `"excludeFromScreenCapture": false` in
   `%LOCALAPPDATA%\BastionVault\settings.json` before capturing, and **restore it afterwards** —
   after the process has fully exited, because the app rewrites the file during shutdown and
   will otherwise put the flag it was started with straight back.
2. Start `BastionVault.exe` with the `--test-pick-*` flags for whatever pickers the run needs,
   plus `--trace-bindings=<file>`.
3. Drive it from PowerShell with `UIAutomationClient` / `UIAutomationTypes`. Most controls
   carry an `AutomationProperties.Name` or `AutomationId` (`PasswordField`, `ConfirmField`,
   `CreateButton`, `UnlockButton`, `List`, `Tree`, `AddressBar`, `StatusBar`). A `PasswordBox`
   deliberately exposes no `ValuePattern`, so type into it with `SetFocus()` plus
   `SendKeys`/`SendInput`. Four things bite every time:
   - Only the **content** command-bar buttons carry `AutomationId = <keymap id>` (`NewFolder`,
     `ImportFiles`, `ImportFolder`, `Export`, `Cut`…`Redo`); they come from the templated
     group. The shell commands on the right — Save, Verify, Lock — are written out by hand in
     `CommandBarView.xaml` with an `AutomationProperties.Name` only, so match them by name
     **and** control type.
   - The window's own title-bar Close is `AutomationId = CloseButtonElement` and is *named*
     "Close", exactly like the button on the import and verify report dialogs. A bare
     name lookup finds the title bar first and shuts the app down mid-run.
   - The New-vault dialog opens with no path and `Create vault` stays disabled until it has
     one, so `--test-pick-vault-create` is only consulted after the dialog's first
     "Choose..." button is invoked. The blocking reason under the button says which
     requirement is still missing.
   - "The vault has unsaved changes" is not text: it is the title-bar bullet
     (`AutomationId = DirtyBullet`) and the status-bar chip (`PendingChip`). Wait on those,
     not on a word.
   - Rows of the entry list are `ControlType.DataItem`, not `ListItem` (a `ListView` with a
     `GridView` reports its items that way), and folders in the tree are `TreeItem`; a name
     lookup with the wrong control type finds nothing and the run silently types into
     whatever window is in front. The demo vault starts dirty, so Lock (Ctrl+Shift+L) asks
     "Lock with N unsaved changes?" first; invoke "Lock without saving" by name.
   - Whatever `SendKeys` types goes to the foreground window. Check that the dialog you expect
     is actually open (find one of its controls) before typing a password into it.
4. Capture with `System.Drawing.Graphics.CopyFromScreen` over the window's
   `BoundingRectangle` plus a small margin for the DWM shadow.
5. Read the binding-trace file at the end. Anything past the header line is a defect.

**Trap on a developer machine:** other software registers *global* hotkeys, which no
application ever sees. On the machine this was integrated on, `Ctrl+Shift+E`,
`Ctrl+Shift+I` and `Ctrl+Shift+C` were already taken, so Export, Import folder and Copy path
appeared dead from the keyboard while working perfectly from the command bar. Before
reporting a shortcut as broken, check it with `RegisterHotKey`: a failure with error 1409
(`ERROR_HOTKEY_ALREADY_REGISTERED`) means the key never reaches Bastion Vault.

---------------------------------------------------------------------------
## 5. Ownership map

```
src/BastionVault.Core/                 net10.0, no UI dependency. API.md is its contract.
  Crypto/       Argon2, Blake2b, ChunkCipher, HeaderCipher, KeyMaterial, VaultKeys
  Format/       VaultHeader, VaultIndex, IndexSerializer, PadLadder, EntryNames, VaultPath, VaultLimits
  Session/      VaultSession (+ .Persistence), TreeModel, StagingStore, SaveWriter,
                Importer, Exporter, Verifier, UndoStack, KdfPreflight
  seams         IRandomSource, IClock, IVaultPaths — the only places Core touches
                randomness, time and file naming. Swap them in tests, never in the App.

src/BastionVault.App/                  net10.0-windows, WPF, x64, PerMonitorV2.
  App.xaml.cs   composition root: DI graph, crash handlers, culture, single instance, CLI
  Themes/       Lamplight: Tokens, Typography, Icons, HighContrast, Controls/*.xaml
  Shell/        ShellWindow, WindowChromeBehavior, TitleBar, StateStripe, DialogHost,
                StartView, UnlockView, FirstRunView
  Dialogs/      the twelve dialogs and their views
  Views/        the explorer: ExplorerView + command bar, address bar, tree, list,
                preview, status bar, empty states
  ViewModels/   ShellViewModel, OperationViewModel, StartViewModel, UnlockViewModel;
                ExplorerViewModel and friends; Dialogs/*
  Services/     every interface of UI-CONTRACT.md section 5 and its implementation
  Behaviors/    drop, drag, tree drop, column sort, inline rename, focus ring
  Converters/   byte size, relative date, glyphs, visibility, state pip
```

Two rules keep the App honest, and both are worth re-reading before a change:
**no view model references a WPF type** (every OS touchpoint is an interface in
`Services/`), and **Core is never called on the UI thread for long work** — long operations
go through `OperationViewModel.RunAsync`, and Core's `Changed` event is marshalled by
`VaultChangeMarshaller`.

The keymap has a single source, `Input/KeyMap.cs`. The shell binds the `Global` rows;
`ExplorerView` binds the `Explorer` rows from the same table, and a test asserts that every
`Explorer` row has a command in `ExplorerViewModel.ShortcutCommands` — so a new row without
a command fails the build instead of becoming a dead key. The Shortcuts dialog renders the
same table, so it cannot drift from the real bindings.

---------------------------------------------------------------------------
## 6. Where the app writes

| Path | What |
|------|------|
| `%LOCALAPPDATA%\BastionVault\settings.json` | `AppSettings`, atomic write |
| `%LOCALAPPDATA%\BastionVault\recent.dat` | recent vaults, DPAPI-protected |
| `%LOCALAPPDATA%\BastionVault\rollback.dat` | last-seen save counters, DPAPI-protected |
| `%LOCALAPPDATA%\BastionVault\logs\` | rolling text log — never an entry name, in-vault path, key, salt or id |
| `%LOCALAPPDATA%\BastionVault\staging\` | fallback staging, only when `StagingLocation` is not `BesideVault` |
| beside the vault | `<name>.bastion.tmp-<hex>` while saving, `<name>.bastion~stage-<guid>` while staging |

Every run writes `Starting (pid N)` first and `Exiting with code N (pid N)` last, and every
close of the window names its trigger: the title bar button, the Exit command, a system close
command (Alt+F4, the system menu, the taskbar, or UI Automation's `WindowPattern.Close`), or a
bare close message from another program (`taskkill` without `/F`, for one). A Windows log-off
or shutdown is recorded too. So a `Starting` with no `Exiting` for the same process id means the process died hard —
killed, a stack overflow, a native fault, an out-of-memory abort in the runtime — and the
Windows Application event log is the place to look next: `.NET Runtime` event 1026 carries the
exception, `Application Error` event 1000 the faulting module and exception code, and `Windows
Error Reporting` event 1001 the report bucket. Attach both to issue #21 if the exit was not
yours; Windows' `RADAR_PRE_LEAK` reports under 1001 are a memory-growth heuristic, not an
exit, and are expected around large key-derivation presets.

The temporary files are the vault's own directory by default and are removed on a
successful save. `IVaultFactory.SweepOrphansAsync` reclaims ones left by a crash. **Nothing
plaintext is ever written outside an export directory** — worth re-checking after any change
to `SaveWriter`, `StagingStore` or `Exporter`.

---------------------------------------------------------------------------
## 7. Known limitations

- **Video preview is a still frame, not a player (#33).** The pane shows one frame from about
  a tenth of the way in (at most ten seconds), the resolution and the running time; playback
  would need a frame server or a third-party stack and is a separate decision. Codec coverage
  is whatever Windows can decode: H.264 in MP4/MOV, AVI and MKV work out of the box, HEVC needs
  Microsoft's extension, VP9/AV1 in WebM the optional Web Media Extensions. A container Windows
  does not recognise, or a Windows N without the Media Feature Pack, gets a note and the export
  hint instead of a hex dump. The probe runs on a thread-pool thread and is cancelled through
  the stream when the selection moves on, but a demuxer that spins without reading cannot be
  interrupted from outside; nothing of the kind has been seen, and the preview switch in
  Settings is the way out if it ever is.
- **Layout rules worth knowing** (each one was an open item after 1.0 and is now a pinned rule)
  - The side panes follow the window (#18): above 1180 px the tree and the preview keep the
    widths the user gave them; between 1180 and 1000 px both shrink in proportion towards
    160 / 200 px; below 1000 px the preview folds away (`ExplorerViewModel.IsPreviewCollapsedByWidth`,
    the remembered *Preview* choice is untouched) and comes back when the window widens, or at
    once when the user asks for it (Toggle preview, Enter on a file). `ExplorerView.PaneWidthsFor`
    is the pure rule; the splitter positions are remembered for the session as before.
  - The hex preview puts 16 bytes on a line when the pane can show 63 monospace columns and 8
    otherwise (#19), re-rendered from the bytes already held when the pane is resized; the ASCII
    column is never the part that falls off the edge.
  - Column order is persisted with widths and sort (#23); headers can be dragged, the status
    rail is pinned first, and a persisted layout naming a column the build no longer has is
    ignored (`Behaviors/ColumnOrder.cs`).
  - The crash handler shows its own window (`Shell/CrashWindow.cs`) with en-US buttons (#24);
    the native `MessageBox` is only the fallback when that window itself cannot be shown.
  - The window title keeps the vault name while locked and appends "(locked)" (#25), so it
    agrees with the vault chip and the taskbar still identifies the window.
- **Not exercised end to end**
  - Drag and drop is unit-tested through `DropAsync`/`CanDrop`, but no synthesised drag has
    been screenshotted, so the drag adorner and the 700 ms tree hover-expand are unverified
    visually.
  - The image preview has never been seen with real image bytes. Its *failure* path is
    covered: an STA test drives a poisoned byte array through `ImagePreview.Rebuild`.
  - High-contrast hot-swap (`Services/ThemeController.cs`) is implemented and registered but
    has not been screenshotted under an actual high-contrast theme.
  - (Closed by #27.) The Argon2 lane join and the two post-save reopen windows now have
    test-only seams: `Argon2.HashWithSegmentHook` (internal) runs a hook at the start of every
    segment fill, and `VaultSession.TestHooks` / `SaveTestHooks` run before the verification
    reopen and before the session's own reopen. Neither changes a byte; the golden fixtures
    prove it. `Crypto/Argon2LaneJoinTests.cs` and `Session/SaveReopenTests.cs` use them.
- **By design**
  - Whole-file rollback stays undetectable (THREAT-MODEL A2); only the save counter signals
    it, and the unlock screen warns.
  - `MarqueeSelectionBehavior` was listed as optional and was not built.

### Left open deliberately

Everything below was reported in the review round, judged, and *not* changed. Each one is a
decision waiting to be made, not a defect nobody noticed.

- **The KDF pre-flight answers "could a machine this size serve it", not "can it right now".**
  FORMAT.md section 3.1 step 9 compares the header's `memoryKiB` against
  `KdfMemoryFractionOfInstalled` (0.75) of the memory the machine physically has. Measuring free
  memory instead was tried and reverted: it refused the default Standard preset (512 MiB) with
  `ResourceLimit` on a 32 GiB machine that happened to have under a gigabyte free during the final
  smoke run. The other half of that story is now handled without changing the rule: a KDF that
  passes the pre-flight and then fails to allocate leaves Core as `ResourceLimit` (#16), the
  unlock card keeps the figures and offers *Try again* (#17), and the preset pickers mark a preset
  this machine cannot serve and preselect the largest one that fits (#13, #17). The question
  itself is public as `KdfPreflight.Check`, so the UI shows Core's verdict rather than its own.
- **The KDF phase is not interruptible.** API.md's cancellation table states normatively that
  Open, Unlock, VerifyPassword and ChangeCredentials report `IsCancellable = false` for the
  whole derivation. Making it abortable at pass boundaries is a contract change to API.md and
  the UI, so it needs a decision rather than a patch.
- **An index may declare a `chunkSize` far larger than the file it describes.** Nothing in
  FORMAT.md section 4.6 ties the two together, so rejecting it would refuse legal v1 vaults.
  The amplification is gone anyway: `BlobReader` now publishes the real maximum chunk length
  and every reader sizes its pooled buffers from that, not from the declared number.
- **The "one lamp" rule (UI-CONTRACT.md section 1.9) was decided (#28, option A).** The
  checked/selected state of a control (checkbox fill, radio dot, toggle knob, menu check,
  active sort chevron, active tab rail, caret and text selection) is *allowed* to be amber:
  a selected state is "something is live" in the sense of the rule. Section 1.9 names them
  and carries the review criterion; new controls are judged against that list, not against
  the shorter original wording. Still not enforced by a test: a resource-dictionary audit
  would be the tool, and nobody has asked for it.
- **Single-instance identity keys on the file id (#20).** The mutex and pipe names of a vault
  that exists derive from its volume serial number plus 128-bit file id, so a junction, a
  mapped drive or a UNC alias is the same vault. A vault that does not exist yet is guarded
  by its normalised path until it does, and the shell re-acquires the lock right after
  `CreateAsync` so the new file is guarded by its id as well; every opener checks both names.
  The remaining window is the few milliseconds between the create finishing and the
  re-acquire, and the save-time conflict detection stays the safety net for it.
