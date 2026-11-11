namespace RPGModder.Core.Workspace;

public sealed class WorkspaceMigrationResult
{
    public int ImportedFiles { get; set; }
    public int ImportedMods { get; set; }
    public int ImportedProfiles { get; set; }
    public bool SchemaUpgraded { get; set; }
    public List<string> Warnings { get; } = new();
}
