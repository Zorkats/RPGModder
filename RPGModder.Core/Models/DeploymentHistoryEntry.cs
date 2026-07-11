using Newtonsoft.Json;

namespace RPGModder.Core.Models;

public sealed class DeploymentHistoryEntry
{
    [JsonProperty("timestampUtc")]
    public DateTime TimestampUtc { get; set; }

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("enabledMods")]
    public List<string> EnabledMods { get; set; } = new();

    [JsonProperty("diagnostics")]
    public List<OperationDiagnostic> Diagnostics { get; set; } = new();

    public string Status => Success ? "Completed" : "Rolled back";
    public int EnabledModCount => EnabledMods.Count;
    public string TimestampDisplay => TimestampUtc.ToLocalTime().ToString("g");
    public string Summary => Success
        ? $"Deployed {EnabledModCount} mod(s)"
        : Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ?? "Deployment failed";
}

