using BastionVault.App.Services;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Resolves the on-screen order of the entry list's keyed columns from what <c>settings.json</c> remembers
/// (<see cref="ColumnLayout"/>). Pure, so it is testable without a <c>GridView</c>: a persisted layout may
/// name a column this build no longer has (ignored), miss one it gained (appended in build order), or have
/// been written by hand with duplicate positions (stable sort keeps the build order for ties).
/// </summary>
public static class ColumnOrder
{
    /// <summary>Orders <paramref name="currentKeys"/> by the persisted positions.</summary>
    /// <param name="currentKeys">The keyed columns the build has, in their XAML order.</param>
    /// <param name="saved">The persisted column states.</param>
    /// <returns>The keys in the order they should appear, left to right.</returns>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> currentKeys, IReadOnlyList<ColumnState> saved)
    {
        ArgumentNullException.ThrowIfNull(currentKeys);
        ArgumentNullException.ThrowIfNull(saved);

        var known = new List<(string Key, int Order, int BuildIndex)>();
        var unknown = new List<(string Key, int BuildIndex)>();

        for (int i = 0; i < currentKeys.Count; i++)
        {
            string key = currentKeys[i];
            ColumnState? state = saved.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                unknown.Add((key, i));
            }
            else
            {
                known.Add((key, state.Order, i));
            }
        }

        // Persisted positions first, ties broken by build order; columns the layout does not know come
        // after them in build order, so a column added in a newer build appears rather than vanishes.
        return
        [
            .. known.OrderBy(k => k.Order).ThenBy(k => k.BuildIndex).Select(k => k.Key),
            .. unknown.OrderBy(u => u.BuildIndex).Select(u => u.Key),
        ];
    }
}
