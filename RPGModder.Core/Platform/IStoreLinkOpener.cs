namespace RPGModder.Core.Platform;

/// <summary>Hands a store link such as <c>steam://validate/&lt;appid&gt;</c> to the store's client (TST-19 seam).</summary>
public interface IStoreLinkOpener
{
    /// <summary>Returns false when no client took the link.</summary>
    bool Open(string link);
}
