namespace QuickTransfer.Framework;

internal readonly record struct ChestSlot(uint ItemId, int Quantity, bool IsHq)
{
    public bool IsEmpty => ItemId == 0;
    public bool HasItem => ItemId != 0 && Quantity > 0;
}

internal readonly record struct ChestMove(int SrcPage, int SrcSlot, int DstPage, int DstSlot);

internal static class CompanyChestPlanner
{
    private static readonly string[] BusyPhrases =
    [
        "Another player is using the chest",
        "Unable to store item",
        "Unable to complete company chest action"
    ];

    public static bool IsBusyText(string text) => BusyPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    // The open quantity prompt still reads the HandleItemMove buffers, so they are never freed while it is visible.
    public static bool ShouldReleaseMoveBuffers(bool inputVisible, bool sawInput, long createdAtMs, long nowMs)
        => !inputVisible && (sawInput || createdAtMs > 0 && nowMs - createdAtMs >= 1500);

    // A non-full stack and a later stack of the same item and HQ flag to merge into it.
    public static ChestMove? FindMergeMove(IReadOnlyList<ChestSlot[]> pages, Func<uint, uint> stackSize)
    {
        for (var dp = 0; dp < pages.Count; dp++)
        {
            for (var di = 0; di < pages[dp].Length; di++)
            {
                var d = pages[dp][di];
                if (!d.HasItem)
                {
                    continue;
                }

                var maxStack = stackSize(d.ItemId);
                if (maxStack <= 1 || (int)maxStack - d.Quantity <= 0)
                {
                    continue;
                }

                for (var sp = dp; sp < pages.Count; sp++)
                {
                    for (var si = sp == dp ? di + 1 : 0; si < pages[sp].Length; si++)
                    {
                        var s = pages[sp][si];
                        if (s.HasItem && s.ItemId == d.ItemId && s.IsHq == d.IsHq)
                        {
                            return new(sp, si, dp, di);
                        }
                    }
                }
            }
        }

        return null;
    }

    // The first empty slot and the next occupied slot after it.
    public static ChestMove? FindCompactionMove(IReadOnlyList<ChestSlot[]> pages)
    {
        for (var dp = 0; dp < pages.Count; dp++)
        {
            for (var di = 0; di < pages[dp].Length; di++)
            {
                if (!pages[dp][di].IsEmpty)
                {
                    continue;
                }

                for (var sp = dp; sp < pages.Count; sp++)
                {
                    for (var si = sp == dp ? di + 1 : 0; si < pages[sp].Length; si++)
                    {
                        if (pages[sp][si].HasItem)
                        {
                            return new(sp, si, dp, di);
                        }
                    }
                }

                return null;
            }
        }

        return null;
    }

    // One selection-sort step on a single page: move the smallest later key into the first out-of-order slot.
    public static (int Src, int Dst)? FindSortMove(ChestSortKey[] keys, bool[] empty)
    {
        var size = keys.Length;
        if (size <= 1)
        {
            return null;
        }

        var firstEmpty = Array.IndexOf(empty, true);
        if (firstEmpty >= 0)
        {
            var nextOccupied = Array.IndexOf(empty, false, firstEmpty + 1);
            if (nextOccupied >= 0)
            {
                return (nextOccupied, firstEmpty);
            }
        }

        for (var i = 0; i < size && !empty[i]; i++)
        {
            var best = i;
            for (var j = i + 1; j < size && !empty[j]; j++)
            {
                if (keys[j].CompareTo(keys[best]) < 0)
                {
                    best = j;
                }
            }

            if (best != i)
            {
                return (best, i);
            }
        }

        return null;
    }

    public static (int Page, int Slot)? FindFirstEmptySlot(IReadOnlyList<ChestSlot[]> pages)
    {
        for (var p = 0; p < pages.Count; p++)
        {
            var i = Array.FindIndex(pages[p], s => s.IsEmpty);
            if (i >= 0)
            {
                return (p, i);
            }
        }

        return null;
    }

    // The matching stack with the most free space.
    public static (int Page, int Slot)? FindBestStackSlot(IReadOnlyList<ChestSlot[]> pages, uint itemId, bool isHq, uint maxStack)
    {
        if (itemId == 0 || maxStack <= 1)
        {
            return null;
        }

        (int Page, int Slot)? best = null;
        var bestFree = 0;
        for (var p = 0; p < pages.Count; p++)
        {
            for (var i = 0; i < pages[p].Length; i++)
            {
                var s = pages[p][i];
                if (!s.HasItem || s.ItemId != itemId || s.IsHq != isHq)
                {
                    continue;
                }

                var free = (int)maxStack - s.Quantity;
                if (free > bestFree)
                {
                    bestFree = free;
                    best = (p, i);
                }
            }
        }

        return best;
    }
}
