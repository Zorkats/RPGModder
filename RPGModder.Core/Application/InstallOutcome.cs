using RPGModder.Core.Models;

namespace RPGModder.Core.Application;

/// <summary>
/// The result of an install: the diagnostics, and on success the installed folder and its manifest.
/// </summary>
public sealed record InstallOutcome(OperationResult Result, string? FolderName, ModManifest? Manifest);
