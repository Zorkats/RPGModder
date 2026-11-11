using System.Diagnostics;
using Newtonsoft.Json;
using RPGModder.Core.Models;
using RPGModder.Core.Services;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The only reader and writer of <c>.rpgmodder/deployed.json</c>. A missing or unreadable file means
/// the deployed state is unknown, and the UI then shows the profile as pending.
/// </summary>
public sealed class DeployedStateStore
{
    private readonly string _path;

    public DeployedStateStore(GameWorkspacePaths workspace)
    {
        _path = workspace.DeployedState;
    }

    public DeployedState? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
            DeployedState? state = JsonConvert.DeserializeObject<DeployedState>(File.ReadAllText(_path), settings);
            if (state == null || state.SchemaVersion > DeployedState.CurrentSchemaVersion)
            {
                return null;
            }

            return state;
        }
        catch (JsonException ex)
        {
            // An unreadable file carries no state, which is exactly what "unknown" means.
            Trace.TraceWarning($"deployed.json is not valid JSON and is treated as unknown: {ex.Message}");
            return null;
        }
    }

    public void Write(DeployedState state)
    {
        FileTreeService.WriteAllTextAtomic(_path, JsonConvert.SerializeObject(state, Formatting.Indented));
    }

    public void Forget()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
