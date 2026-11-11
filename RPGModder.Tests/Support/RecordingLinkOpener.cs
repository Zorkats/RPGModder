using RPGModder.Core.Platform;

namespace RPGModder.Tests.Support;

/// <summary>Records the store links a test asked to open, instead of opening them.</summary>
public sealed class RecordingLinkOpener : IStoreLinkOpener
{
    public List<string> Opened { get; } = new();

    public bool Open(string link)
    {
        Opened.Add(link);
        return true;
    }
}
