# Bastion.App — UI contract and design language "Lamplight"

`Bastion.App` is a WPF application (net10.0-windows, x64, CommunityToolkit.Mvvm 8.4,
Microsoft.Extensions.DependencyInjection). This document fixes the rules, tokens,
resource keys, service interfaces and the shell/explorer split so that two people can
build the App in parallel without merging each other's files.

---------------------------------------------------------------------------
## 1. Hard rules (review checklist)

1. **No ViewModel references WPF types** (`Window`, `Dispatcher`, `MessageBox`, `Clipboard`,
   `Application.Current`, anything in `System.Windows.*`). Every OS touchpoint is behind an
   interface in `Services/`. ViewModels are tested in `tests/Bastion.App.Tests` without STA.
2. **Core is never called on the UI thread for long work.** Long operations go through
   `OperationViewModel.RunAsync(...)`, which wraps `Task.Run`, a `ThrottledProgress<VaultProgress>`
   (80 ms `DispatcherTimer`, `Background` priority) and the cancel command. Core's `Changed`
   event is marshalled by `VaultChangeMarshaller` onto the dispatcher.
3. **Passwords never become `string`.** Views read `PasswordBox.SecurePassword` and build a
   `Passphrase` via `PasswordBoxBinder.ToPassphrase` in code-behind on submit; ViewModels take
   a `Passphrase` parameter and dispose it. No "reveal" that mirrors into a `TextBox`; the
   reveal button toggles a second `PasswordBox`-free preview only while pressed using
   `PasswordBox.PasswordChar` swap is **not** available — so reveal is implemented as a
   `TextBox` that is created only while the button is held, filled from `SecurePassword`, and
   cleared/destroyed on release; `IsUndoEnabled=False`. `TextBox.IsUndoEnabled=False` on
   rename and comment editors too.
4. **`AllowsTransparency` is forbidden.** Chrome: `WindowChrome` with `CaptionHeight=40`,
   `ResizeBorderThickness=6`, `GlassFrameThickness="0,1,0,0"`, `UseAeroCaptionButtons=False`,
   `NonClientFrameEdges=None`; rounded corners, dark title bar and border colour via
   `DwmSetWindowAttribute` (`DWMWA_WINDOW_CORNER_PREFERENCE=33 → 2`, `DWMWA_USE_IMMERSIVE_DARK_MODE=20 → 1`,
   `DWMWA_BORDER_COLOR=34 → 0x0041322A`). Maximised margin from `GetSystemMetricsForDpi`
   (`SM_CXSIZEFRAME + SM_CXPADDEDBORDER`) re-applied on `StateChanged`/`DpiChanged`.
   `WM_NCHITTEST` hook returns `HTMAXBUTTON` over the maximize button (Win11 snap layouts).
   `WM_GETMINMAXINFO` handled so a maximised window respects the work area.
5. **Every `GridViewColumn` has an explicit width.** ListView:
   `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`,
   `CacheLength=2,2 Page`, `SelectionMode=Extended`, hosted in a `*` row, never in a
   `ScrollViewer`. Sorting via `ListCollectionView.CustomSort` with `EntryComparer`
   (folders first, `StrCmpLogicalW` natural order). Lists are replaced wholesale
   (`IReadOnlyList<EntryItemViewModel>` property) — never thousands of `Add`s.
6. **TreeView is VM-driven:** `IsExpanded`/`IsSelected` are `[ObservableProperty]` on
   `FolderNodeViewModel`, two-way bound in `ItemContainerStyle`; children load lazily on first
   expand from `IVaultSession.GetChildren`; no code touches a `TreeViewItem`.
7. **Drop handlers do no work inside the OLE loop:** copy `string[]` from `DataFormats.FileDrop`,
   set `Handled`, `Dispatcher.BeginInvoke(Background, …)` the import. Internal drag carries
   `EntryId`s in the `DataObject`, never containers. Drag-out to Explorer is refused with an
   adorner text ("Use Export (Ctrl+Shift+E) to write files to disk.").
8. **In-window dialogs are modal for real:** `DialogHost` sets `IsEnabled=False` on the shell
   content root, `KeyboardNavigation.TabNavigation=Cycle` + focus scope on the card,
   `AutomationProperties.IsDialog=True`, focuses the first field, restores focus on close,
   Escape = cancel (no-op during non-cancellable phases), `Window.Closing` is cancelled while
   `IsBusy`. OS pickers (`Microsoft.Win32.OpenFileDialog/SaveFileDialog/OpenFolderDialog`) are
   real windows shown with the shell as owner and `FOS_DONTADDTORECENT` behaviour where
   available (set `DereferenceLinks=false`, and do not add to recent via `JumpList`).
9. **The one-lamp rule:** the accent `#F2A93B` appears only where something is *live,
   focused, selected or unsaved*: the primary action of the current dialog, the focus ring,
   the selection rail, pending-change pips, the state stripe, the vault chip, and the
   checked/selected state of a control (checkbox fill, radio dot, toggle knob, menu check,
   active sort chevron, active tab rail, text caret and text selection). Everything else is
   greyscale: no amber on icons at rest, headers, links, hovers of unselected rows, dividers,
   or type icons.
10. **Lock clears state:** on lock the tree/list/preview are removed from the visual tree,
    search text, navigation history, internal clipboard and preview buffers are cleared, the
    title resets to "Bastion", `SetWindowDisplayAffinity` is dropped (lock screen may be captured).
11. **No OS clipboard for vault content.** Ctrl+X/C/V use the internal clipboard
    (`IInternalClipboard`). The OS clipboard receives only "Copy path" and "Copy details" text,
    tagged with `ExcludeClipboardContentFromMonitorProcessing` and `CanIncludeInClipboardHistory=0`.
12. **Text rendering:** `TextOptions.TextFormattingMode=Ideal`, `TextRenderingMode=ClearType`,
    `UseLayoutRounding=True`, `SnapsToDevicePixels=True` on the shell root; every panel has an
    opaque `Background`.
13. **Logging** (`Serilog`-free, own tiny `ILog`): rolling text file under
    `%LOCALAPPDATA%\Bastion\logs`, never an entry name, in-vault path, key, salt or id.
14. **All brushes are `DynamicResource`** so a High Contrast dictionary can be swapped in when
    `SystemParameters.HighContrast` is true (`Themes/HighContrast.xaml` maps tokens to `SystemColors`).

---------------------------------------------------------------------------
## 2. Design tokens (resource keys, `Themes/Tokens.xaml`)

### Colours (`Color.*`) and brushes (`Brush.*`, same suffix)
| Key                    | Value    | Use                                                        |
|------------------------|----------|------------------------------------------------------------|
| `Bg0`                  | #0E1116  | window ground                                              |
| `Bg1`                  | #151922  | panels (tree, list, preview, dialogs)                      |
| `Bg2`                  | #1C222D  | cards, hovered rows, inputs                                |
| `Bg3`                  | #232B38  | pressed rows, menu hover                                   |
| `StrokeDivider`        | #2A3241  | decorative seams (light half of the etched seam)           |
| `StrokeShadow`         | #0B0E13  | shadow half of the etched seam                             |
| `StrokeControl`        | #626E86  | outlines of interactive controls (3.1:1+ on all grounds)   |
| `StrokeControlHover`   | #7B879F  |                                                            |
| `TextPrimary`          | #E7EAF0  |                                                            |
| `TextSecondary`        | #9AA3B2  | captions, column headers, hints                            |
| `TextDisabled`         | #767F90  |                                                            |
| `TextMono`             | #C9D1DE  | Cascadia Mono readouts                                     |
| `Accent`               | #F2A93B  | the lamp                                                   |
| `AccentHover`          | #FFBE5C  |                                                            |
| `AccentPressed`        | #D9922B  |                                                            |
| `AccentDim`            | #8A6222  | state stripe "unlocked & saved"                            |
| `OnAccent`             | #1A1408  | text on amber fills                                        |
| `FocusOuter`           | #FFD48A  | 2 px ring outside the control                              |
| `FocusInner`           | #0E1116  | 1 px ring inside the control                               |
| `SelectRest`           | #2B2724  | selected row (active window)                               |
| `SelectInactive`       | #232833  | selected row (inactive window)                             |
| `DangerText`           | #FF6B6F  | red text and icons                                         |
| `DangerFill`           | #B32830  | destructive button fill (white text)                       |
| `Success`              | #3DD68C  |                                                            |
| `Warning`              | #F5C542  |                                                            |
| `Info`                 | #4CA6FF  |                                                            |
| `Scrim`                | #A60A0C10| dialog backdrop (65 % alpha)                               |

### Typography (`Text.*` styles; families exist on Windows 11)
| Key          | Family                          | Size/Line | Weight    | Use                                   |
|--------------|---------------------------------|-----------|-----------|---------------------------------------|
| `Caption`    | Segoe UI Variable Small, Segoe UI | 11/16   | Regular   | status bar, column headers, timestamps |
| `Body`       | Segoe UI Variable Text, Segoe UI  | 13/20   | Regular   | default                                |
| `BodyStrong` | Segoe UI Variable Text, Segoe UI  | 13/20   | SemiBold  |                                        |
| `Subtitle`   | Segoe UI Variable Text, Segoe UI  | 16/22   | SemiBold  | dialog section headers                 |
| `Title`      | Segoe UI Variable Display, Segoe UI | 20/28 | SemiBold  | dialog titles, empty-state headlines   |
| `Hero`       | Segoe UI Variable Display, Segoe UI | 28/36 | Light     | first-run                              |
| `Mono`       | Cascadia Mono, Consolas           | 12/18   | Regular   | every cryptographic quantity, uppercase hex grouped in fours |
| `SectionLabel` | Segoe UI Variable Small        | 11/16   | SemiBold  | UPPERCASE, +8 % tracking, `TextSecondary`, followed by a hairline (rule-and-caps header) |

### Spacing / sizes / radii / motion
- Spacing ramp 2 / 4 / 8 / 12 / 16 / 24 / 32 / 48. Window padding 12, card 20, dialog 24, related controls 8, groups 16.
- Heights: title bar 40, command bar 44, breadcrumb 32, list row 28 (Compact 24 / Spacious 32, persisted), input 32, command-bar button 32, dialog primary button 36, status bar 26.
- Radii: `Radius.Small`=4 (buttons, chips), `Radius.Medium`=6 (cards, menus, inputs), `Radius.Dialog`=8. Window radius comes from DWM.
- Icon sizes 16 (lists, menus), 20 (command bar), 32/48 (empty states).
- Motion: 120 ms ease-out (0.2,0,0,1) hover/press; 180 ms fade + 8 px rise for overlays; none when `SystemParameters.ClientAreaAnimation` is false.

### Signature details (implement all)
1. **Etched seam** (`Style.Seam`): every divider is 2 px — 1 px `StrokeShadow` above 1 px `StrokeDivider`.
2. **Status rail**: leftmost 12 px list column carries pending pips (filled amber dot = added, ring = changed, red dot = failed verify); folders in the tree show a 4 px dot when any descendant is pending.
3. **Instrument typography**: KDF parameters, salt-free ids, byte counts, throughput, derivation time rendered in `Text.Mono`. The unlock button reads "Deriving key · Argon2id · 512 MiB · 3 passes" instead of spinning.
4. **State stripe**: 2 px full-width bar under the title bar: none = no vault; `StrokeControl` = locked; `AccentDim` = unlocked & saved; amber dashed = unsaved; amber shimmer = operation running; `DangerText` = integrity failure.
5. **Chamfered vault chip**: the vault-name chip in the title bar and the unlock card have an 8 px 45° chamfer on top-left and bottom-right corners (Path geometry). Nothing else is chamfered.
6. **Rule-and-caps section headers** in dialogs and the details pane.
7. **Blueprint empty states**: 1 px amber line art at 20 % opacity (vault door, open folder, magnifier), drawn as `Path` geometry.
8. **One-lamp rule** (§1.9).

### Icons (`Segoe Fluent Icons`, fallback `Segoe MDL2 Assets`; glyph resource keys `Glyph.*`)
New vault E8A6 (+ amber "+" badge) · Open E8E5 · Save E74E · Save as E792 · Import files E8B5 ·
Import folder E8B6 · Export EDE1 · New folder E8F4 · Rename E8AC · Delete E74D · Verify EA18
(Shield; F760 ShieldTask when Fluent present) · Change password E8D7 · Lock E72E · Unlocked E785 ·
Settings E713 · Back E72B · Forward E72A · Up E74A · Search E721 · Chevron E76C / E970 · Sort E70E/E70D ·
Folder E8B7 · Folder open E838 · File E7C3 · Document E8A5 · Image E91B · Code E943 · Archive E7B8 ·
Video E714 · Audio EC4F · Keyfile EB95 · Cut E8C6 · Copy E8C8 · Paste E77F · Select all E8B3 ·
Properties E946 · Undo E7A7 · Redo E7A6 · OK F13E · Warning E7BA · Error EA39 · Info E946 ·
Reveal E9A8/E9A9 · Preview E8FF · Keyboard EDA7 · Minimize E921 · Maximize E922 · Restore E923 · Close E8BB.
16 px in lists/menus, 20 px in the command bar; `TextSecondary` at rest, `TextPrimary` on hover; type icons at 60 % opacity.

---------------------------------------------------------------------------
## 3. Keymap (single source; also rendered in the Shortcuts dialog)

| Keys                          | Action                                       |
|-------------------------------|----------------------------------------------|
| Ctrl+N / Ctrl+O / Ctrl+S      | New vault / Open vault / Save                |
| Ctrl+Shift+S                  | Save a copy…                                 |
| Ctrl+Shift+L                  | Lock                                         |
| Ctrl+L, Alt+D, F4             | Focus & edit the address bar                 |
| Ctrl+I / Ctrl+Shift+I         | Import files / Import folder                 |
| Ctrl+Shift+E                  | Export selection… (Export all when nothing is selected) |
| Ctrl+Shift+N                  | New folder                                   |
| F2 / Enter / Esc              | Rename / open folder or preview file / cancel rename, clear search |
| Delete                        | Delete (no confirmation; undoable)           |
| Ctrl+Z / Ctrl+Y               | Undo / Redo                                  |
| Ctrl+X / Ctrl+C / Ctrl+V      | Internal cut / copy / paste (Ctrl+V with Explorer files on the OS clipboard = import) |
| Ctrl+Shift+C                  | Copy in-vault path                           |
| Ctrl+A                        | Select all                                   |
| Alt+Left / Alt+Right / Alt+Up / Backspace / Alt+Home | Back / Forward / Up / Up / Root; mouse XButton1/2 = Back/Forward |
| Ctrl+F, Ctrl+E                | Focus search                                 |
| Alt+Enter                     | Properties                                   |
| Space                         | Preview focused item                         |
| F6 / Shift+F6                 | Cycle focus tree → list → address → preview  |
| Shift+F10, Menu               | Context menu at focused item                 |
| Ctrl+Shift+V                  | Verify                                       |
| Ctrl+Shift+H                  | Panic: hide preview, mask names              |
| F1, ?                         | Keyboard shortcuts                           |
| Ctrl+,                        | Settings                                     |

---------------------------------------------------------------------------
## 4. Shell state machine (`ShellViewModel.Mode`)

`NoVault` (start screen with Create / Open / recents) → `Unlocking` (KDF running, not
cancellable) → `Open` (explorer) ⇄ `Locked` (unlock card, same path) ; `Open` → `Busy`
(modal operation: Save, Save copy, Change credentials) → `Open`. Background-capable
operations (Import, Export, Verify, Recover) keep `Open` and show inline progress in the
status bar + state stripe; mutating commands are disabled while one runs.

Auto-lock (`IAutoLockController`): system-wide idle via `GetLastInputInfo` (5 s poll),
and immediately on `SessionSwitch(SessionLock/RemoteDisconnect/ConsoleDisconnect)`,
`PowerModeChanged(Suspend)`, `SessionEnding`. Auto-lock never saves and never prompts:
it calls `IVaultSession.Lock()`; unsaved work stays in the session and reappears on unlock.
Manual lock with unsaved changes prompts Save / Lock without saving / Cancel.
Closing the window with unsaved changes prompts Save / Discard N changes / Cancel.
Unhandled exceptions: `ZeroKeys()` first, then report.

---------------------------------------------------------------------------
## 5. Service interfaces (`Services/`, all in namespace `Bastion.App.Services`)

```csharp
public interface IDialogService
{
    Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> dialog, CancellationToken ct = default);
    Task<ConfirmResult> ConfirmAsync(ConfirmRequest request);          // title (verb + count), body, buttons with verbs, destructive = non-default
    Task ShowErrorAsync(string title, string message, string? details = null);
    Task ShowInfoAsync(string title, string message);
}
public interface IFileDialogService
{
    string? PickVaultToOpen(); string? PickVaultToCreate(string suggestedName); string? PickKeyFile(); string? PickKeyFileToCreate();
    IReadOnlyList<string> PickFilesToImport(); string? PickFolderToImport(); string? PickExportFolder();
}
public interface ISettingsService { AppSettings Current { get; } void Save(); event EventHandler? Changed; }   // JSON under %LOCALAPPDATA%\Bastion\settings.json, atomic write
public interface IRecentVaults { IReadOnlyList<RecentVault> Items { get; } void Touch(string path); void Forget(string path); void Clear(); }  // DPAPI-protected file
public interface IRollbackGuard { ulong? LastSeenCounter(string vaultIdHex); void Record(string vaultIdHex, ulong counter); }             // DPAPI-protected file
public interface IInternalClipboard { ClipboardOp? Content { get; } void Set(IReadOnlyList<EntryId> ids, bool isCut); void Clear(); event EventHandler? Changed; }
public interface IOsClipboard { void SetText(string text); IReadOnlyList<string>? GetFileDropList(); bool HasFileDrop { get; } }
public interface IIdleMonitor { TimeSpan Idle { get; } event EventHandler? IdleThresholdReached; TimeSpan Threshold { get; set; } bool Enabled { get; set; } }
public interface ISystemEvents { event EventHandler? SessionLocked; event EventHandler? Suspending; event EventHandler? SessionEnding; }
public interface IShellIntegration { void RegisterFileAssociation(); void UnregisterFileAssociation(); bool IsRegistered { get; } void ApplyProcessHygiene(); }  // AppUserModelID, jump list off
public interface ISingleInstance { IDisposable? TryAcquireVault(string path); void FocusExistingInstance(string path); }
public interface IScreenPrivacy { void SetExcludeFromCapture(bool exclude); }
public interface IClock { DateTimeOffset UtcNow { get; } }       // reuse Bastion.Core.IClock
public interface IUiDispatcher { void Post(Action action); bool CheckAccess(); }
public interface ILog { void Info(string message); void Warn(string message, Exception? ex = null); void Error(string message, Exception? ex = null); }
public interface IKdfEstimator { Task<TimeSpan> EstimateAsync(KdfParameters p, CancellationToken ct); }     // wraps KdfBenchmark, caches per parameters
```

`AppSettings`: `Theme` (Dark|HighContrastAuto), `AutoLockMinutes` (default 10, 0 = off),
`DefaultKdfPreset`, `RowDensity`, `ColumnLayout` (widths/order/sort), `WindowPlacement`
(validated against current monitors on restore), `RememberRecentVaults` (default true),
`RememberKeyFilePaths` (default false), `StagingLocation` (BesideVault|SystemTemp|Custom),
`StagingCustomPath`, `ExcludeFromScreenCapture` (default true), `PreviewEnabled` (true),
`MaskNamesWhenInactive` (false), `BlurPreviewWhenInactive` (true), `SizeObfuscation` (false),
`ReencryptOnSave` (false), `ShowFirstRun` (true).

---------------------------------------------------------------------------
## 6. Project layout and ownership

```
src/Bastion.App/
  App.xaml / App.xaml.cs         composition root (DI), exception handlers, single instance, CLI arg   [shell]
  app.manifest                   PerMonitorV2, longPathAware, asInvoker, Win10/11 supportedOS         [shell]
  Bastion.App.csproj             (orchestrator owns)
  Assets/bastion.ico             (exists)
  Themes/  Tokens.xaml, Typography.xaml, Icons.xaml, HighContrast.xaml,
           Controls/*.xaml (Button, ToggleButton, TextBox, PasswordBox, ComboBox, CheckBox, RadioButton,
           ScrollBar, ScrollViewer, Menu, ContextMenu, MenuItem, ToolTip, ProgressBar, Slider,
           TreeView, ListView+GridView (headers, gripper), TabControl, Separator/Seam, Expander),
           Lamplight.xaml (merges all)                                                                [shell]
  Shell/   ShellWindow.xaml(.cs), WindowChromeBehavior.cs, TitleBarView.xaml, StateStripe.xaml,
           DialogHost.xaml(.cs), StartView.xaml (NoVault), UnlockView.xaml, FirstRunView.xaml         [shell]
  Dialogs/ NewVaultDialog, ChangePasswordDialog, PropertiesDialog, ProgressDialog, ConfirmDialog,
           NameConflictDialog, VerifyReportDialog, ImportReportDialog, SettingsDialog, AboutDialog,
           ShortcutsDialog, PendingChangesPopover  (+ ViewModels/Dialogs/*)                          [shell]
  Views/   ExplorerView.xaml (3 columns + splitters), CommandBarView.xaml, AddressBarView.xaml,
           FolderTreeView.xaml, EntryListView.xaml, PreviewPaneView.xaml, StatusBarView.xaml,
           EmptyStateView.xaml                                                                       [explorer]
  ViewModels/ ShellViewModel, OperationViewModel, StartViewModel, UnlockViewModel                     [shell]
              ExplorerViewModel, FolderNodeViewModel, EntryItemViewModel, AddressBarViewModel,
              PreviewViewModel, StatusBarViewModel, CommandBarViewModel, NavigationHistory          [explorer]
              Dialogs/*                                                                              [shell]
  Services/ all interfaces above + implementations, PasswordBoxBinder, ThrottledProgress,
            VaultChangeMarshaller, JsonSettingsService, DpapiStore                                    [shell]
            InternalClipboard, NaturalStringComparer (StrCmpLogicalW), EntryComparer, FileTypeCatalog [explorer]
  Behaviors/ FileDropBehavior, ListDragBehavior, TreeDropBehavior, ColumnSortBehavior,
             InlineRenameBehavior, MarqueeSelectionBehavior (optional), FocusRingBehavior            [explorer]
  Converters/ ByteSize, RelativeDate, EntryKindToGlyph, FileTypeToGlyph, BoolToVisibility, StateToPip [explorer]
tests/Bastion.App.Tests/         FakeVaultSession, ViewModel tests, converter tests                   [tests]
```

Contract between the two App owners: `ShellViewModel` exposes `IVaultSession? Session`,
`ShellMode Mode`, `OperationViewModel Operation`, `IAsyncRelayCommand` for global commands
(New/Open/Save/SaveCopy/Lock/ChangeCredentials/Verify/Settings/Exit) and creates
`ExplorerViewModel` (constructor: `IVaultSession session, IDialogService, IFileDialogService,
IInternalClipboard, IOsClipboard, ISettingsService, IUiDispatcher, ILog, OperationViewModel`).
`ExplorerViewModel` exposes `CurrentFolder`, `Items`, `SelectedItems`, `Tree`, `AddressBar`,
`Preview`, `StatusBar` and the entry commands. Theme resource keys are exactly those in §2 — the
explorer owner may not add colour tokens; new styles go in `Views/*.xaml` resources.

---------------------------------------------------------------------------
## 7. Dialog flows (summary)

- **New vault**: file path (Save picker), password + confirm (`PasswordBox`), Caps Lock banner,
  hold-to-reveal, KDF-calibrated strength sentence ("At Standard, eight high-end GPUs would need
  about N years…") from a zxcvbn-style estimator (own implementation: patterns, dictionary of top
  10k passwords embedded, dates, sequences, repeats), hard minimum 8 characters, preset radio
  (Fast/Standard/Strong) with measured estimate, optional keyfile (Choose… / Generate…),
  required checkbox "I understand that if I lose this password, nobody can recover this vault."
- **Unlock**: password, optional keyfile (remembered path opt-in), header info line in `Text.Mono`
  ("Argon2id · 512 MiB · 3 passes · needs 512 MiB RAM"), three distinct error messages per
  FORMAT.md §9, select-all instead of clearing on failure, 1 s soft delay after 3 failures,
  "last saved <time> · save #N" after success, rollback warning if counter decreased.
- **Change credentials**: current password required, new password (+ strength), keyfile add/
  remove, preset, mode (default full re-key; "fast, rewrap only" with the honest caveat), cost
  line "will rewrite N GB on save, about M minutes", becomes pending; applied on Save.
- **Progress**: verb, current item (middle ellipsis), n of N, bytes, MB/s, ETA after 2 s, cancel
  semantics sentence, Cancel disables + relabels "Finishing — can't cancel" in the non-cancellable window.
- **Confirm**: title = verb + count; buttons are verbs; destructive is non-default; no "don't ask again".
- **Name conflict**: Replace / Skip / Keep both, "do this for all N".
- **Verify report**: files, bytes, elapsed, throughput, failures with paths; re-openable from status bar.
- **Properties**: entry or vault (counts, bytes, on-disk size, KDF parameters in Mono, save counter, last saved, size obfuscation, re-encrypt-on-save).
- **Settings**: everything in `AppSettings`, plus "Register .bastion file type", "Clear recent vaults".
- **First run**: one screen: save model, no recovery, where the file lives.
- **Empty states**: no vault, empty folder, no results, locked.
