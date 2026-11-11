using RPGModder.Core.Models;

namespace RPGModder.Core.Workspace;

/// <summary>Profiles and the active-profile marker of one workspace (OWN-5). Tests replace it to fail writes.</summary>
public interface IProfileStore
{
    IReadOnlyList<string> List(GameWorkspacePaths workspace);

    /// <summary>Reads a profile and migrates it to the current version in memory. A missing file reads as an empty profile.</summary>
    ModProfile Load(GameWorkspacePaths workspace, string profileName);

    /// <summary>False when the profile, or the file it would replace, is newer than this build (GR-V2).</summary>
    bool CanWrite(GameWorkspacePaths workspace, string profileName, ModProfile profile);

    /// <summary>Writes the profile atomically at the current version. Throws <see cref="InvalidDataException"/> when <see cref="CanWrite"/> is false.</summary>
    void Save(GameWorkspacePaths workspace, string profileName, ModProfile profile);

    void Delete(GameWorkspacePaths workspace, string profileName);

    string ReadActive(GameWorkspacePaths workspace);

    void WriteActive(GameWorkspacePaths workspace, string profileName);
}
