using RPGModder.Core.Models;

namespace RPGModder.Core.Merge;

/// <summary>One enabled mod. <c>ModId</c> is the folder name (GR-M3).</summary>
public sealed record ModSource(
    string ModId,
    int Priority,
    IReadOnlyList<ModFile> Files,
    IReadOnlyList<ModPatch> Patches,
    IReadOnlyList<PluginEntry> Plugins);
