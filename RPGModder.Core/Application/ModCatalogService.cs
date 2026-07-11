using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Core.Application;

public sealed record ModCatalogSnapshot(
    IReadOnlyList<ModListItem> Mods,
    IReadOnlyList<OperationDiagnostic> Diagnostics);

public interface IModCatalogService
{
    ModCatalogSnapshot Load(GameWorkspacePaths workspace, ModProfile profile);
    void Invalidate(string? manifestPath = null);
}

public sealed class ModCatalogService : IModCatalogService
{
    private readonly Dictionary<string, CachedManifest> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModCatalogSnapshot Load(GameWorkspacePaths workspace, ModProfile profile)
    {
        var diagnostics = new List<OperationDiagnostic>();
        var discovered = new Dictionary<string, ModListItem>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(workspace.Mods))
        {
            return new ModCatalogSnapshot(Array.Empty<ModListItem>(), diagnostics);
        }

        foreach (string directory in Directory.GetDirectories(workspace.Mods))
        {
            string folderName = Path.GetFileName(directory);
            if (folderName.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            string manifestPath = Path.Combine(directory, "mod.json");
            if (!File.Exists(manifestPath))
            {
                diagnostics.Add(new OperationDiagnostic(
                    "manifest.missing",
                    "The mod folder does not contain mod.json.",
                    DiagnosticSeverity.Warning,
                    folderName));
                continue;
            }

            try
            {
                ModManifest manifest = LoadManifest(manifestPath);
                discovered[folderName] = new ModListItem(
                    manifest,
                    folderName,
                    profile.EnabledMods.Contains(folderName, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new OperationDiagnostic(
                    "manifest.invalid",
                    ex.Message,
                    DiagnosticSeverity.Error,
                    folderName));
            }
        }

        var ordered = new List<ModListItem>();
        foreach (string folderName in profile.LoadOrder)
        {
            if (discovered.Remove(folderName, out ModListItem? mod))
            {
                ordered.Add(mod);
            }
        }

        ordered.AddRange(discovered.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        for (int index = 0; index < ordered.Count; index++)
        {
            ordered[index].LoadOrder = index;
        }

        return new ModCatalogSnapshot(ordered, diagnostics);
    }

    public void Invalidate(string? manifestPath = null)
    {
        if (manifestPath == null)
        {
            _cache.Clear();
            return;
        }

        _cache.Remove(Path.GetFullPath(manifestPath));
    }

    private ModManifest LoadManifest(string path)
    {
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (_cache.TryGetValue(fullPath, out CachedManifest? cached) &&
            cached.LastWriteUtc == info.LastWriteTimeUtc &&
            cached.Length == info.Length)
        {
            return cached.Manifest;
        }

        var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(fullPath))
                       ?? throw new InvalidDataException("mod.json is empty or invalid.");
        _cache[fullPath] = new CachedManifest(info.LastWriteTimeUtc, info.Length, manifest);
        return manifest;
    }

    private sealed record CachedManifest(DateTime LastWriteUtc, long Length, ModManifest Manifest);
}
