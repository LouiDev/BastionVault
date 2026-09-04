# Developing Bastion

How to build it, test it, run it, and where everything lives. The normative documents are
`API.md` (the frozen `Bastion.Core` surface), `UI-CONTRACT.md` (the App's rules and the
"Lamplight" design language), `FORMAT.md` (the on-disk format) and `THREAT-MODEL.md`.
This file is the working manual; it decides nothing.

---------------------------------------------------------------------------
## 1. Prerequisites

- **.NET SDK 10.0** (the repo builds with 10.0.400).
- **Windows 10 1809 or later**, x64. `Bastion.App` is `net10.0-windows` with `UseWPF`;
  only `Bastion.Core` (`net10.0`) is portable, and only it has tests that would run
  anywhere.
- No other tooling. `Directory.Packages.props` pins every package version centrally;
  there are exactly three: CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection
  and the test stack (xUnit, NSubstitute).

---------------------------------------------------------------------------
## 2. Build and test

```
dotnet build Bastion.slnx
dotnet test  Bastion.slnx
```

The tree is expected to build with **zero warnings**; treat a new one as a build break.
A full run is about 15 seconds:

| Project              | Tests | Covers                                                    |
|----------------------|-------|-----------------------------------------------------------|
| `Bastion.Core.Tests` |  724  | crypto vectors, the format, the session, the tamper matrix, golden fixtures |
| `Bastion.App.Tests`  |  232  | view models, converters, the keymap, and one real end-to-end run |

Useful filters:

```
dotnet test tests/Bastion.Core.Tests --filter "FullyQualifiedName~Vault.BlobTamper"
dotnet test tests/Bastion.App.Tests  --filter "FullyQualifiedName~EndToEnd"
```

### The end-to-end test

`tests/Bastion.App.Tests/EndToEnd/RealVaultEndToEndTests.cs` is the one test that proves the
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
BASTION_REGEN_GOLDEN=1 dotnet test tests/Bastion.Core.Tests          # bash
$env:BASTION_REGEN_GOLDEN='1'; dotnet test tests/Bastion.Core.Tests  # PowerShell
```

`dotnet test -- --regenerate-golden` does **not** work: VSTest does not forward arguments
after `--` to xUnit v2. Never regenerate a fixture to turn a red test green — a difference
means either the format changed deliberately (and `FORMAT.md` says so) or a writer that must
be deterministic no longer is, which is a real bug. `tests/fixtures/README.md` lists exactly
what is pinned in each fixture.

---------------------------------------------------------------------------
## 3. Running the app

```
dotnet run --project src/Bastion.App                          # start screen
dotnet run --project src/Bastion.App -- C:\path\to\my.bastion  # open a vault at start-up
```

The built executable is `src/Bastion.App/bin/Debug/net10.0-windows/Bastion.exe`
(the assembly is named `Bastion`, not `Bastion.App`).

### Demo mode

```
dotnet run --project src/Bastion.App -- --demo
dotnet run --project src/Bastion.App -- --demo C:\vaults\demo.bastion
```

`--demo` swaps `Bastion.Core`'s factory for an in-memory `FakeVaultSession`
(`Services/Demo/`), so every screen can be reached without a real vault: any password
unlocks, and the fake save takes about three seconds on purpose so the progress card, its
ETA and the non-cancellable tail are all visible. Pass a path as well to land on the unlock
card instead of the start screen. Demo mode holds no key material, which is why the
view-model boundary accepts a nullable `Passphrase`; the real path never passes null.

### Test hooks (Debug builds only)

Both hooks below are compiled out of Release builds (`#if DEBUG` in `App.xaml.cs`), so a
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
   `%LOCALAPPDATA%\Bastion\settings.json` before capturing, and **restore it afterwards** —
   after the process has fully exited, because the app rewrites the file during shutdown and
   will otherwise put the flag it was started with straight back.
2. Start `Bastion.exe` with the `--test-pick-*` flags for whatever pickers the run needs,
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
4. Capture with `System.Drawing.Graphics.CopyFromScreen` over the window's
   `BoundingRectangle` plus a small margin for the DWM shadow.
5. Read the binding-trace file at the end. Anything past the header line is a defect.

**Trap on a developer machine:** other software registers *global* hotkeys, which no
application ever sees. On the machine this was integrated on, `Ctrl+Shift+E`,
`Ctrl+Shift+I` and `Ctrl+Shift+C` were already taken, so Export, Import folder and Copy path
appeared dead from the keyboard while working perfectly from the command bar. Before
reporting a shortcut as broken, check it with `RegisterHotKey`: a failure with error 1409
(`ERROR_HOTKEY_ALREADY_REGISTERED`) means the key never reaches Bastion.

---------------------------------------------------------------------------
## 5. Ownership map

```
src/Bastion.Core/                 net10.0, no UI dependency. API.md is its contract.
  Crypto/       Argon2, Blake2b, ChunkCipher, HeaderCipher, KeyMaterial, VaultKeys
  Format/       VaultHeader, VaultIndex, IndexSerializer, PadLadder, EntryNames, VaultPath, VaultLimits
  Session/      VaultSession (+ .Persistence), TreeModel, StagingStore, SaveWriter,
                Importer, Exporter, Verifier, UndoStack, KdfPreflight
  seams         IRandomSource, IClock, IVaultPaths — the only places Core touches
                randomness, time and file naming. Swap them in tests, never in the App.

src/Bastion.App/                  net10.0-windows, WPF, x64, PerMonitorV2.
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
| `%LOCALAPPDATA%\Bastion\settings.json` | `AppSettings`, atomic write |
| `%LOCALAPPDATA%\Bastion\recent.dat` | recent vaults, DPAPI-protected |
| `%LOCALAPPDATA%\Bastion\rollback.dat` | last-seen save counters, DPAPI-protected |
| `%LOCALAPPDATA%\Bastion\logs\` | rolling text log — never an entry name, in-vault path, key, salt or id |
| `%LOCALAPPDATA%\Bastion\staging\` | fallback staging, only when `StagingLocation` is not `BesideVault` |
| beside the vault | `<name>.bastion.tmp-<hex>` while saving, `<name>.bastion~stage-<guid>` while staging |

The temporary files are the vault's own directory by default and are removed on a
successful save. `IVaultFactory.SweepOrphansAsync` reclaims ones left by a crash. **Nothing
plaintext is ever written outside an export directory** — worth re-checking after any change
to `SaveWriter`, `StagingStore` or `Exporter`.

---------------------------------------------------------------------------
## 7. Known limitations

- **Cosmetic and small**
  - The command bar drops its labels to glyphs below a breakpoint, but the list columns do
    not reflow with it: the tree (248 px) and the preview pane (320 px) keep their width, so
    at the declared 880 px minimum the four columns still need more room than the middle pane
    gets and a horizontal scrollbar remains. Making the side panes responsive is the real fix
    and is a larger layout change than the label breakpoint was.
  - The hex preview is 16 bytes to a line, so the ASCII column needs a preview pane wider
    than the 320 px default.
  - Column *order* is not persisted and reordering is off; widths and sort are persisted.
  - The crash handler is a native `MessageBox`, so its buttons follow the OS language even
    though the rest of the UI is pinned to en-US.
  - The window title resets to "Bastion" while the vault is locked, but the vault-name chip
    stays in the custom title bar. The unlock card shows the full path anyway, so this leaks
    nothing new.
- **Not exercised end to end**
  - Drag and drop is unit-tested through `DropAsync`/`CanDrop`, but no synthesised drag has
    been screenshotted, so the drag adorner and the 700 ms tree hover-expand are unverified
    visually.
  - The image preview has never been seen with real image bytes. Its *failure* path is
    covered: an STA test drives a poisoned byte array through `ImagePreview.Rebuild`.
  - High-contrast hot-swap (`Services/ThemeController.cs`) is implemented and registered but
    has not been screenshotted under an actual high-contrast theme.
  - Two Core fixes carry no dedicated regression test, because both need a failure injected
    inside a private, non-seamed path and the seam would have meant restructuring code the
    review asked to leave alone: the Argon2 lane join when lane 0 throws, and a vault file
    that cannot be reopened in the window between `File.Replace` and the post-save reopen.
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
  smoke run. What remains open is the other half of that story — a KDF that passes the pre-flight
  and then fails to allocate still surfaces as a raw `OutOfMemoryException` (see below), nothing
  retries, and nothing suggests a cheaper preset.
- **The KDF phase is not interruptible.** API.md's cancellation table states normatively that
  Open, Unlock, VerifyPassword and ChangeCredentials report `IsCancellable = false` for the
  whole derivation. Making it abortable at pass boundaries is a contract change to API.md and
  the UI, so it needs a decision rather than a patch.
- **The unlock card states the RAM the vault needs but never warns.** It reads
  "Argon2id · 512 MiB · 3 passes · needs 512 MiB RAM" whether or not the machine can afford
  it; the refusal only arrives after Unlock is pressed.
- **`OutOfMemoryException` is not translated.** `IoGuard.Translate` handles `IOException`,
  `CryptographicException` and `ArgumentOutOfRangeException` (API.md rule 5); an OOM from the
  pinned Argon2 allocation escapes as itself. Allocating a wrapper during an OOM is its own
  hazard, which is why it was left alone.
- **An index may declare a `chunkSize` far larger than the file it describes.** Nothing in
  FORMAT.md section 4.6 ties the two together, so rejecting it would refuse legal v1 vaults.
  The amplification is gone anyway: `BlobReader` now publishes the real maximum chunk length
  and every reader sizes its pooled buffers from that, not from the declared number.
- **The "one lamp" rule (UI-CONTRACT.md section 1.9) is not enforced.** Amber is currently
  also the checkbox fill, radio dot, slider, toggle knob, menu check, sort chevron, tab rail
  and the caret and selection brushes. Moving all of those to greyscale would rework the
  design language on the strength of a "likely" finding; amending section 1.9 to name the
  selection affordances that are allowed to be amber is the other option, and the one the
  App side recommends.
- **Single-instance identity is still the uppercased path.** The same vault reached through a
  junction, a mapped drive or a UNC path is two instances. Deriving the mutex and pipe name
  from the file id (volume serial plus file index) would fix it, but needs a decision about a
  vault that does not exist yet.
