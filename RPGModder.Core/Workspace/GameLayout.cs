using RPGModder.Core.Services;

namespace RPGModder.Core.Workspace;

/// <summary>
/// Where this game keeps <c>data</c>, <c>js</c> and <c>save</c>, and which engine it runs (OWN-2).
/// Detected once when the session opens; no other type probes for a <c>www</c> folder.
/// </summary>
/// <param name="GameRoot">The folder that holds the game executable.</param>
/// <param name="ContentRoot">The game root, or its <c>www</c> folder for a deployed MV game.</param>
/// <param name="UsesWww">True when the content lives under <c>www</c>.</param>
/// <param name="Engine">MV or MZ from the engine's core script, or null when neither is present.</param>
/// <param name="SaveFolder">The live save folder: <c>save</c> under the content root, unless only the game root has one.</param>
public sealed record GameLayout(string GameRoot, string ContentRoot, bool UsesWww, RpgMakerEngine? Engine, string SaveFolder)
{
    private const string WwwFolder = "www";
    private const string SaveFolderName = "save";

    public static GameLayout Detect(string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot);
        string www = Path.Combine(root, WwwFolder);
        bool usesWww = Directory.Exists(www) &&
                       (Directory.Exists(Path.Combine(www, "js")) || Directory.Exists(Path.Combine(www, "data")));
        string contentRoot = usesWww ? www : root;

        string saveFolder = Path.Combine(contentRoot, SaveFolderName);
        string rootSave = Path.Combine(root, SaveFolderName);
        if (!Directory.Exists(saveFolder) && Directory.Exists(rootSave))
        {
            saveFolder = rootSave;
        }

        return new GameLayout(root, contentRoot, usesWww, DetectEngine(contentRoot), saveFolder);
    }

    private static RpgMakerEngine? DetectEngine(string contentRoot)
    {
        string js = Path.Combine(contentRoot, "js");
        if (File.Exists(Path.Combine(js, "rmmz_core.js")))
        {
            return RpgMakerEngine.MZ;
        }

        if (File.Exists(Path.Combine(js, "rpg_core.js")))
        {
            return RpgMakerEngine.MV;
        }

        return null;
    }
}
