namespace RPGModder.Tests.Support;

/// <summary>A game folder written by <see cref="RpgProjectBuilder"/>.</summary>
public sealed record RpgGame(string GameRoot, string ContentRoot, string ExecutablePath, RpgEngine Engine, bool UsesWww)
{
    public string SaveFolder => Path.Combine(ContentRoot, "save");

    public string ContentFile(string relativePath)
    {
        return Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ReadContent(string relativePath)
    {
        return File.ReadAllText(ContentFile(relativePath));
    }
}
