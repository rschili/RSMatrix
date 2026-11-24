using System.Collections.ObjectModel;

namespace RSMatrix.Desktop.ViewModels;

internal static class CollectionSync
{
    /// <summary>
    /// Makes the collection equal to the desired items with moves, inserts and removals, so items that stay keep
    /// their containers and selection.
    /// </summary>
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;
            var existing = IndexOf(target, desired[i], i + 1);
            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start) where T : class
    {
        for (var i = start; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
                return i;
        }
        return -1;
    }
}
