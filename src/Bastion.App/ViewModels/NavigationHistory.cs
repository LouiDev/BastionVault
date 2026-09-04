using Bastion.Core;

namespace Bastion.App.ViewModels;

/// <summary>
/// Back and forward for the folder the explorer is showing. It is a browser history, not a stack
/// of parents: navigating somewhere new truncates the forward branch, and the whole thing is
/// bounded at <see cref="Capacity"/> entries so a long session cannot pin folder ids for ever.
/// Lock clears it (UI-CONTRACT.md section 1.10).
/// </summary>
public sealed class NavigationHistory
{
    /// <summary>How many places the history remembers before it forgets the oldest.</summary>
    public const int Capacity = 64;

    private readonly List<EntryId> _places = [];
    private int _index = -1;

    /// <summary>Raised whenever <see cref="CanGoBack"/> or <see cref="CanGoForward"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the history currently stands, or <see langword="null"/> when it is empty.</summary>
    public EntryId? Current => _index >= 0 ? _places[_index] : null;

    /// <summary>True when there is somewhere to go back to.</summary>
    public bool CanGoBack => _index > 0;

    /// <summary>True when a back step can be undone.</summary>
    public bool CanGoForward => _index >= 0 && _index < _places.Count - 1;

    /// <summary>Everything the history holds, oldest first. Exposed for tests and diagnostics.</summary>
    public IReadOnlyList<EntryId> Places => _places;

    /// <summary>
    /// Records a navigation to <paramref name="folder"/>. Re-visiting the current folder is
    /// ignored, and anything ahead of the cursor is dropped.
    /// </summary>
    /// <param name="folder">The folder that is now being shown.</param>
    public void Visit(EntryId folder)
    {
        if (Current == folder)
        {
            return;
        }

        if (_index < _places.Count - 1)
        {
            _places.RemoveRange(_index + 1, _places.Count - _index - 1);
        }

        _places.Add(folder);

        if (_places.Count > Capacity)
        {
            _places.RemoveAt(0);
        }

        _index = _places.Count - 1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Steps back and returns where to go, or <see langword="null"/> when there is nowhere.</summary>
    public EntryId? Back()
    {
        if (!CanGoBack)
        {
            return null;
        }

        _index--;
        Changed?.Invoke(this, EventArgs.Empty);
        return _places[_index];
    }

    /// <summary>Steps forward and returns where to go, or <see langword="null"/> when there is nowhere.</summary>
    public EntryId? Forward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        _index++;
        Changed?.Invoke(this, EventArgs.Empty);
        return _places[_index];
    }

    /// <summary>Drops every entry, including the current one.</summary>
    public void Clear()
    {
        if (_places.Count == 0)
        {
            return;
        }

        _places.Clear();
        _index = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes folders that no longer exist, for example after a delete or an undo. The cursor
    /// stays on the nearest surviving place.
    /// </summary>
    /// <param name="exists">Answers whether a folder id is still resolvable.</param>
    public void Prune(Func<EntryId, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        EntryId? current = Current;
        int removedBeforeCursor = 0;

        for (int i = _places.Count - 1; i >= 0; i--)
        {
            if (exists(_places[i]))
            {
                continue;
            }

            _places.RemoveAt(i);
            if (i <= _index)
            {
                removedBeforeCursor++;
            }
        }

        if (removedBeforeCursor == 0)
        {
            return;
        }

        _index = Math.Min(_index - removedBeforeCursor, _places.Count - 1);
        if (_index < 0 && _places.Count > 0)
        {
            _index = 0;
        }

        if (Current != current)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
