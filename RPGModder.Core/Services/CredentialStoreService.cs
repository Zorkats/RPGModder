using System.Security.Cryptography;
using System.Text;

namespace RPGModder.Core.Services;

public interface ICredentialStoreService
{
    bool IsAvailable { get; }
    string Store(string rawSecret);
    string Retrieve(string storedValue);
    void Delete(string? storedValue = null);
    bool IsSecureValue(string storedValue);
}

public sealed class CredentialStoreService : ICredentialStoreService
{
    private const string DpapiPrefix = "dpapi:";
    private const string SecretServiceReference = "secret-service:rpgmodder/nexus-api-key";
    private const string ApplicationAttribute = "RPGModder";
    private const string CredentialAttribute = "nexus-api-key";

    private readonly string? _secretTool;

    public CredentialStoreService()
    {
        _secretTool = OperatingSystem.IsLinux() ? PlatformService.FindExecutable("secret-tool") : null;
    }

    public bool IsAvailable => OperatingSystem.IsWindows() ||
                               (OperatingSystem.IsLinux() && _secretTool != null);

    public string Store(string rawSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawSecret);

        if (OperatingSystem.IsWindows())
        {
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(rawSecret), null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(encrypted);
        }

        if (OperatingSystem.IsLinux() && _secretTool != null)
        {
            bool stored = PlatformService.Run(_secretTool,
            [
                "store", "--label=RPGModder Nexus Mods API Key",
                "application", ApplicationAttribute,
                "credential", CredentialAttribute
            ], rawSecret + Environment.NewLine, timeoutMilliseconds: 30_000);

            if (stored)
                return SecretServiceReference;

            throw new InvalidOperationException(
                "Linux Secret Service rejected the credential. Ensure your desktop keyring is installed and unlocked.");
        }

        throw new PlatformNotSupportedException(
            "Secure credential storage is unavailable. Install libsecret (secret-tool) to save a Nexus API key on Linux.");
    }

    public string Retrieve(string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
            return string.Empty;

        if (storedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                return string.Empty;

            try
            {
                byte[] encrypted = Convert.FromBase64String(storedValue[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    encrypted, null, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return string.Empty; }
            catch (FormatException) { return string.Empty; }
        }

        if (storedValue.Equals(SecretServiceReference, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsLinux() || _secretTool == null)
                return string.Empty;

            return PlatformService.Capture(_secretTool,
            [
                "lookup",
                "application", ApplicationAttribute,
                "credential", CredentialAttribute
            ], timeoutMilliseconds: 30_000) ?? string.Empty;
        }

        // Compatibility with v1/v2 settings. Callers should immediately migrate this value.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                byte[] encrypted = Convert.FromBase64String(storedValue);
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        encrypted, null, DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException)
                {
                    return string.Empty;
                }
            }
            catch (FormatException)
            {
                return storedValue;
            }
        }

        return storedValue;
    }

    public void Delete(string? storedValue = null)
    {
        if (OperatingSystem.IsLinux() && _secretTool != null &&
            (storedValue == null || storedValue.Equals(SecretServiceReference, StringComparison.Ordinal)))
        {
            PlatformService.Run(_secretTool,
            [
                "clear",
                "application", ApplicationAttribute,
                "credential", CredentialAttribute
            ], requireSuccess: false);
        }
    }

    public bool IsSecureValue(string storedValue) =>
        storedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal) ||
        storedValue.Equals(SecretServiceReference, StringComparison.Ordinal);
}
