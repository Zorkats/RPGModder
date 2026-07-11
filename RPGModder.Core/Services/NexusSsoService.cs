using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RPGModder.Core.Services;

public class NexusSsoService
{
    private readonly string _applicationSlug;
    private readonly Uri _webSocketUri = new("wss://sso.nexusmods.com");
    private readonly ICredentialStoreService _credentialStore;

    public NexusSsoService(string applicationSlug, ICredentialStoreService? credentialStore = null)
    {
        _applicationSlug = applicationSlug;
        _credentialStore = credentialStore ?? new CredentialStoreService();
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
            if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
        }
    }

    public bool IsSecureStorageAvailable => _credentialStore.IsAvailable;

    public bool IsSecureStoredValue(string storedValue) => _credentialStore.IsSecureValue(storedValue);

    public string EncryptKeyForStorage(string rawKey) => _credentialStore.Store(rawKey);

    public string DecryptKeyFromStorage(string encryptedBase64) => _credentialStore.Retrieve(encryptedBase64);

    public void DeleteStoredKey(string? storedValue = null) => _credentialStore.Delete(storedValue);
}
