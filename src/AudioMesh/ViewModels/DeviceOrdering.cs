using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace AudioMesh.ViewModels;

/// <summary>Ordering rules for the speaker cards.</summary>
public static class DeviceOrdering
{
    /// <summary>
    /// Moves a batch of newly discovered items to the top, keeping the order in which
    /// they appeared. Everything else keeps its relative order underneath, so a
    /// hot-plugged speaker or DAC is visible without shuffling the existing cards.
    /// </summary>
    public static void PromoteToTop<T>(ObservableCollection<T> items, IReadOnlyList<T> newest)
        where T : class
    {
        int top = 0;

        foreach (T item in newest)
        {
            int index = items.IndexOf(item);
            if (index < 0) continue;

            if (index != top) items.Move(index, top);
            top++;
        }
    }
}
