using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPGModder.Core.Services;

public class NexusSsoService
{
    private readonly string _applicationSlug;
    private readonly Uri _webSocketUri = new("wss://sso.nexusmods.com");

    public NexusSsoService(string applicationSlug)
    {
        _applicationSlug = applicationSlug;
    }

    // Main entry point for the authentication flow
    // Returns the plain-text API key if successful, or null if failed/timed out
    public async Task<string?> AuthenticateAsync(Action<string> onStatusUpdate)
    {
        string uuid = Guid.NewGuid().ToString();

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            onStatusUpdate("Connecting to Nexus Mods...");

            await client.ConnectAsync(_webSocketUri, cts.Token);

            // Nexus SSO requires an initial handshake packet with the UUID
            var initMessage = new { id = uuid, token = (string?)null, protocol = 2 };
            string initJson = JsonSerializer.Serialize(initMessage);
            byte[] initBytes = Encoding.UTF8.GetBytes(initJson);

            await client.SendAsync(
                new ArraySegment<byte>(initBytes),
                WebSocketMessageType.Text,
                true,
                cts.Token);

            onStatusUpdate("Waiting for browser authorization...");
            OpenBrowserForAuth(uuid);

            // Start listening loop for the server's response
            return await ListenForAuthTokenAsync(client, cts.Token);
        }
        catch (OperationCanceledException)
        {
            onStatusUpdate("Authentication timed out.");
            return null;
        }
        catch (Exception ex)
        {
            onStatusUpdate($"Connection error: {ex.Message}");
            return null;
        }
        finally
        {
            if (client.State == WebSocketState.Open)
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
            }
        }
    }

    // Listens to the WebSocket stream until the specific auth payload arrives
    private async Task<string?> ListenForAuthTokenAsync(ClientWebSocket client, CancellationToken token)
    {
        var buffer = new byte[4096];

        while (client.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            string responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

            try
            {
                using var document = JsonDocument.Parse(responseJson);
                var root = document.RootElement;

                // Look for a successful response containing the api_key
                if (root.TryGetProperty("success", out var successElement) && successElement.GetBoolean())
                {
                    if (root.TryGetProperty("data", out var dataElement) &&
                        dataElement.TryGetProperty("api_key", out var apiKeyElement))
                    {
                        return apiKeyElement.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed or intermediate ping/pong JSON packets
                continue;
            }
        }

        return null;
    }

    // OS-agnostic method to open the default web browser to the Nexus SSO page
    private void OpenBrowserForAuth(string uuid)
    {
        string url = $"https://www.nexusmods.com/sso?id={uuid}&application={_applicationSlug}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback for Linux environments where UseShellExecute might fail
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
    }

    // Encrypts the raw API key using Windows DPAPI bound to the current user
    // Returns a Base64 string safe for saving to a JSON config file
    public string EncryptKeyForStorage(string rawKey)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // DPAPI is Windows-only. For Linux/Mac compatibility, we fallback to plain text or a different cipher
            return rawKey;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(rawKey);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(encryptedBytes);
    }

    // Decrypts the Base64 API key back to plain text for HTTP requests
    // Decrypts the Base64 API key back to plain text for HTTP requests
    public string DecryptKeyFromStorage(string encryptedBase64)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return encryptedBase64;
        }

        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedBase64);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException)
        {
            // The string is not Base64. This means it's a legacy plain-text key from v1.1.2 or older.
            // We just return it as-is so the app can still log the user in.
            return encryptedBase64;
        }
        catch (CryptographicException)
        {
            // This happens if the file was moved to a different PC or user account
            return string.Empty;
        }
    }
}