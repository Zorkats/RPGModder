using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Core.Application;

public interface IDeploymentHistoryService
{
    IReadOnlyList<DeploymentHistoryEntry> Load(GameWorkspacePaths workspace, int limit = 100);
}

public sealed class DeploymentHistoryService : IDeploymentHistoryService
{
    public IReadOnlyList<DeploymentHistoryEntry> Load(GameWorkspacePaths workspace, int limit = 100)
    {
        workspace.EnsureCreated();
        var entries = new List<DeploymentHistoryEntry>();
        foreach (string file in Directory.GetFiles(workspace.History, "deployment-*.json")
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(1, limit)))
        {
            try
            {
                DeploymentHistoryEntry? entry = JsonConvert.DeserializeObject<DeploymentHistoryEntry>(File.ReadAllText(file));
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
            }
        }

        return entries;
    }
}
