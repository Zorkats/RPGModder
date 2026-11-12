namespace RPGModder.Core.Merge;

/// <summary>
/// One installed mod as <see cref="IdRemapPlanner"/> sees it. A disabled mod still reserves its ids (GR-R5).
/// <c>PluginScripts</c> holds the text of every <c>js/plugins/*.js</c> file the mod ships; a number found only there
/// makes a remap an explicit choice (GR-R11).
/// </summary>
public sealed record RemapMod(
    ModChangeSet ChangeSet,
    bool Enabled,
    IReadOnlyList<string> PluginScripts);
