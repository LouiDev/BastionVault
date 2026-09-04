using System.Windows;
using System.Windows.Media;

namespace Bastion.App.Behaviors;

/// <summary>
/// The two visual-tree walks the explorer behaviours need. They are here rather than repeated in
/// each behaviour, and they are the only place the explorer reaches into the visual tree at all.
/// </summary>
public static class VisualSearch
{
    /// <summary>Walks up from an element to the nearest ancestor of a type.</summary>
    /// <typeparam name="T">Type to look for.</typeparam>
    /// <param name="start">Where to start; may be <see langword="null"/>.</param>
    /// <returns>The ancestor, or <see langword="null"/>.</returns>
    public static T? Ancestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        for (DependencyObject? node = start; node is not null; node = Parent(node))
        {
            if (node is T hit)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>Walks down from an element to the first descendant of a type, optionally by name.</summary>
    /// <typeparam name="T">Type to look for.</typeparam>
    /// <param name="root">Where to start.</param>
    /// <param name="name">Required <c>x:Name</c>, or <see langword="null"/> for any.</param>
    /// <returns>The descendant, or <see langword="null"/>.</returns>
    public static T? Descendant<T>(DependencyObject? root, string? name = null)
        where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is T candidate && (name is null || candidate.Name == name))
            {
                return candidate;
            }

            if (Descendant<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>Every descendant of a type, depth first.</summary>
    /// <typeparam name="T">Type to look for.</typeparam>
    /// <param name="root">Where to start.</param>
    public static IEnumerable<T> Descendants<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
