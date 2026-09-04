using BastionVault.App.Services;
using BastionVault.App.Services.Demo;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>An <see cref="IOsClipboard"/> that records what was written and can hold a file drop.</summary>
public sealed class FakeOsClipboard : IOsClipboard
{
    /// <summary>Everything that was written, newest last.</summary>
    public List<string> Written { get; } = [];

    /// <summary>The file drop the clipboard pretends to hold, or <see langword="null"/> for none.</summary>
    public IReadOnlyList<string>? FileDrop { get; set; }

    /// <inheritdoc />
    public bool HasFileDrop => FileDrop is { Count: > 0 };

    /// <inheritdoc />
    public IReadOnlyList<string>? GetFileDropList() => FileDrop;

    /// <inheritdoc />
    public void SetText(string text) => Written.Add(text);
}

/// <summary>
/// Everything an <see cref="ExplorerViewModel"/> needs, wired to test doubles: the in-memory
/// session the demo host uses, an inline dispatcher so nothing is left pending, and substitutes
/// for the two services that would otherwise put a window on the screen.
/// </summary>
public sealed class ExplorerTestContext : IDisposable
{
    /// <summary>Creates the context and the explorer over a fresh in-memory vault.</summary>
    /// <param name="session">A session to use instead of the default fake one.</param>
    public ExplorerTestContext(IVaultSession? session = null)
    {
        Session = session ?? new FakeVaultSession(@"C:\vaults\demo.bastion");
        Dialogs = Substitute.For<IDialogService>();
        Files = Substitute.For<IFileDialogService>();
        Files.PickFilesToImport().Returns([]);
        Clipboard = new InternalClipboard();
        OsClipboard = new FakeOsClipboard();
        Settings = new MemorySettings();
        Dispatcher = new InlineDispatcher();
        Log = new MemoryLog();
        Operation = new OperationViewModel(Dispatcher, Log);

        Explorer = new ExplorerViewModel(
            Session, Dialogs, Files, Clipboard, OsClipboard, Settings, Dispatcher, Log, Operation);
    }

    /// <summary>The session under the explorer.</summary>
    public IVaultSession Session { get; }

    /// <summary>The dialog service substitute.</summary>
    public IDialogService Dialogs { get; }

    /// <summary>The file picker substitute.</summary>
    public IFileDialogService Files { get; }

    /// <summary>The real internal clipboard.</summary>
    public InternalClipboard Clipboard { get; }

    /// <summary>The recording OS clipboard.</summary>
    public FakeOsClipboard OsClipboard { get; }

    /// <summary>In-memory settings.</summary>
    public MemorySettings Settings { get; }

    /// <summary>A dispatcher that runs everything inline.</summary>
    public InlineDispatcher Dispatcher { get; }

    /// <summary>The in-memory log.</summary>
    public MemoryLog Log { get; }

    /// <summary>The shared operation runner.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>The explorer under test.</summary>
    public ExplorerViewModel Explorer { get; }

    /// <summary>Finds a row of the current listing by name.</summary>
    /// <param name="name">Exact entry name.</param>
    public EntryItemViewModel Item(string name) =>
        Explorer.Items.FirstOrDefault(i => i.RealName == name)
        ?? throw new InvalidOperationException($"No row named '{name}' is listed. Listed: {string.Join(", ", Explorer.Items.Select(i => i.RealName))}");

    /// <summary>Selects rows by name.</summary>
    /// <param name="names">Entry names to select.</param>
    public void Select(params string[] names) => Explorer.SetSelection([.. names.Select(Item)]);

    /// <summary>Runs a search and waits for it, with the debounce turned off.</summary>
    /// <param name="text">What to search for.</param>
    /// <param name="wholeVault">True to search the whole vault.</param>
    public async Task SearchAsync(string text, bool wholeVault = false)
    {
        Explorer.SearchDebounce = TimeSpan.Zero;
        Explorer.SearchWholeVault = wholeVault;
        Explorer.SearchText = text;
        await Explorer.SearchCompletion.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => Explorer.Dispose();
}
