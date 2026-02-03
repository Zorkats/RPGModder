using RPGModder.Core.Models;

namespace RPGModder.Core.Services;

/// <summary>
/// Detects conflicts between mods (files that multiple mods touch)
/// </summary>
public class ConflictDetectionService
{
    /// <summary>
    /// Analyzes all mods and updates their conflict information
    /// </summary>
    public void DetectConflicts(IList<ModListItem> mods)
    {
        // Build a map of file -> list of mods that touch it
        var fileToMods = new Dictionary<string, List<ModListItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            var files = mod.GetAffectedFiles();
            foreach (var file in files)
            {
                if (!fileToMods.ContainsKey(file))
                    fileToMods[file] = new List<ModListItem>();
                fileToMods[file].Add(mod);
            }
        }

        // Now check each mod for conflicts
        foreach (var mod in mods)
        {
            var conflictingFiles = new List<string>();
            var conflictingMods = new HashSet<string>();

            var myFiles = mod.GetAffectedFiles();
            foreach (var file in myFiles)
            {
                if (fileToMods.TryGetValue(file, out var modsForFile) && modsForFile.Count > 1)
                {
                    conflictingFiles.Add(file);
                    foreach (var otherMod in modsForFile)
                    {
                        if (otherMod != mod)
                            conflictingMods.Add(otherMod.Name);
                    }
                }
            }

            mod.HasConflicts = conflictingFiles.Count > 0;
            mod.ConflictingFiles = conflictingFiles;
            mod.ConflictingMods = conflictingMods.ToList();

            if (mod.HasConflicts)
            {
                var tooltip = $"Conflicts with: {string.Join(", ", conflictingMods)}\n" +
                              $"Files: {string.Join(", ", conflictingFiles.Take(3))}";
                if (conflictingFiles.Count > 3)
                    tooltip += $" (+{conflictingFiles.Count - 3} more)";
                mod.ConflictTooltip = tooltip;
            }
            else
            {
                mod.ConflictTooltip = "";
            }
        }
    }

    /// <summary>
    /// Gets detailed conflict information for display
    /// </summary>
    public ConflictReport GenerateReport(IList<ModListItem> mods)
    {
        var report = new ConflictReport();
        
        // Build file -> mods map
        var fileToMods = new Dictionary<string, List<ModListItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Where(m => m.IsEnabled))
        {
            var files = mod.GetAffectedFiles();
            foreach (var file in files)
            {
                if (!fileToMods.ContainsKey(file))
                    fileToMods[file] = new List<ModListItem>();
                fileToMods[file].Add(mod);
            }
        }

        // Find all conflicts
        foreach (var kvp in fileToMods.Where(k => k.Value.Count > 1))
        {
            var conflict = new FileConflict
            {
                FilePath = kvp.Key,
                Mods = kvp.Value.Select(m => m.Name).ToList(),
                Winner = kvp.Value.Last().Name // Last in load order wins
            };
            report.Conflicts.Add(conflict);
        }

        report.HasConflicts = report.Conflicts.Count > 0;
        return report;
    }
}

public class ConflictReport
{
    public bool HasConflicts { get; set; }
    public List<FileConflict> Conflicts { get; set; } = new();
}

public class FileConflict
{
    public string FilePath { get; set; } = "";
    public List<string> Mods { get; set; } = new();
    public string Winner { get; set; } = ""; // The mod that will "win" (last in load order)
}
