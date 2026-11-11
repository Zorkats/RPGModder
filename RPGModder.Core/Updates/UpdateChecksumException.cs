using RPGModder.Core.Operations;

namespace RPGModder.Core.Updates;

/// <summary>The downloaded update has no hash in <c>SHA256SUMS</c>, or not the listed one (TX-13). Nothing was staged.</summary>
public sealed class UpdateChecksumException : IOException
{
    public UpdateChecksumException(string message)
        : base(message)
    {
    }

    public string Code => DiagnosticCodes.UpdateChecksumFailed;
}
