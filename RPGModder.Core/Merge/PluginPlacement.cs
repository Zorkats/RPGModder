namespace RPGModder.Core.Merge;

/// <summary>
/// Places the entries of the merged <c>$plugins</c> list by their <c>@after</c> anchors (GR-C10, seam 11.1).
/// Vanilla entries with no anchor keep their order; nothing is placed by index.
/// </summary>
internal static class PluginPlacement
{
    /// <param name="vanillaOrder">Every vanilla name, in vanilla's order, removed ones included (for the fallback).</param>
    /// <param name="present">Every name of the merged list.</param>
    /// <param name="added">The names vanilla does not have.</param>
    /// <param name="anchors">The <c>@after</c> of each entry that has one; <c>null</c> means "no entry before it".</param>
    public static List<string> Place(
        IReadOnlyList<string> vanillaOrder,
        IReadOnlySet<string> present,
        IReadOnlySet<string> added,
        IReadOnlyDictionary<string, string?> anchors)
    {
        var placed = vanillaOrder.Where(name => present.Contains(name) && !anchors.ContainsKey(name)).ToList();
        var pending = new SortedSet<string>(present.Where(name => !placed.Contains(name)), StringComparer.Ordinal);
        var lastAfter = new Dictionary<string, string>(StringComparer.Ordinal);
        int front = 0;

        while (pending.Count > 0)
        {
            bool progress = false;
            foreach (string name in pending.ToList())
            {
                string? anchor = anchors.GetValueOrDefault(name);
                if (anchor is null)
                {
                    // A new entry with nothing before it goes to the end (GR-C10); a moved one goes first.
                    if (added.Contains(name) || !anchors.ContainsKey(name))
                    {
                        placed.Add(name);
                    }
                    else
                    {
                        placed.Insert(front++, name);
                    }
                }
                else if (placed.Contains(anchor))
                {
                    InsertAfter(placed, lastAfter, anchor, name);
                }
                else if (pending.Contains(anchor))
                {
                    continue;
                }
                else if (NearestPlacedBefore(vanillaOrder, placed, anchor) is string fallback)
                {
                    InsertAfter(placed, lastAfter, fallback, name);
                }
                else
                {
                    placed.Add(name);
                }

                pending.Remove(name);
                progress = true;
            }

            if (!progress)
            {
                // A cycle of anchors, and whatever waits on it: the end of the list, by name.
                placed.AddRange(pending);
                break;
            }
        }

        return placed;
    }

    /// <summary>Right after the anchor, or after the entry last placed there, so entries behind one anchor keep their placing order.</summary>
    private static void InsertAfter(List<string> placed, Dictionary<string, string> lastAfter, string anchor, string name)
    {
        string target = lastAfter.TryGetValue(anchor, out string? last) && placed.Contains(last) ? last : anchor;
        placed.Insert(placed.IndexOf(target) + 1, name);
        lastAfter[anchor] = name;
    }

    /// <summary>The anchor's nearest preceding vanilla entry that is in the list; <c>null</c> when the anchor is not a vanilla entry or none precedes it.</summary>
    private static string? NearestPlacedBefore(IReadOnlyList<string> vanillaOrder, List<string> placed, string anchor)
    {
        int position = -1;
        for (int index = 0; index < vanillaOrder.Count && position < 0; index++)
        {
            if (vanillaOrder[index] == anchor)
            {
                position = index;
            }
        }

        for (int index = position - 1; index >= 0; index--)
        {
            if (placed.Contains(vanillaOrder[index]))
            {
                return vanillaOrder[index];
            }
        }

        return null;
    }
}
