using Newtonsoft.Json;
using System.Linq;
using System.Net.Http.Headers;

namespace RPGModder.Core.Services;

/// <summary>
/// Nexus Mods API client for browsing, downloading, and checking updates.
/// API Documentation: https://app.swaggerhub.com/apis-docs/NexusMods/nexus-mods_public_api_params_in_form_data/1.0
/// </summary>
public class NexusApiService : IDisposable
{
    private readonly HttpClient _http;
    private string? _apiKey;
    private NexusUser? _currentUser;

    // Nexus game domain for RPG Maker games
    // Note: Nexus doesn't have a dedicated RPG Maker category, mods are usually under specific game names
    // We'll support searching across multiple game domains
    public static readonly string[] SupportedGameDomains = new[]
    {
        "rpgmakermv",
        "rpgmakermz", 
        // Add specific game domains as needed
    };

    public bool IsAuthenticated => !string.IsNullOrEmpty(_apiKey) && _currentUser != null;
    public NexusUser? CurrentUser => _currentUser;
    public string? ApiKey => _apiKey;

    public NexusApiService()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.nexusmods.com/v1/")
        };
        _http.DefaultRequestHeaders.Add("Application-Name", "RPGModder");
        _http.DefaultRequestHeaders.Add("Application-Version", "1.3.0");
    }

    /// <summary>
    /// Sets the API key and validates it by fetching user info
    /// </summary>
    public async Task<NexusAuthResult> AuthenticateAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new NexusAuthResult { Success = false, Error = "API key is required" };

        try
        {
            _http.DefaultRequestHeaders.Remove("apikey");
            _http.DefaultRequestHeaders.Add("apikey", apiKey);

            var response = await _http.GetAsync("users/validate.json");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _currentUser = JsonConvert.DeserializeObject<NexusUser>(json);
                _apiKey = apiKey;

                return new NexusAuthResult
                {
                    Success = true,
                    User = _currentUser
                };
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return new NexusAuthResult { Success = false, Error = "Invalid API key" };
            }
            else
            {
                return new NexusAuthResult { Success = false, Error = $"API error: {response.StatusCode}" };
            }
        }
        catch (Exception ex)
        {
            return new NexusAuthResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get latest added mods
    /// </summary>
    public async Task<NexusSearchResult> GetLatestModsAsync(string gameDomain)
    {
        if (!IsAuthenticated)
            return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/latest_added.json");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var mods = JsonConvert.DeserializeObject<List<NexusMod>>(json) ?? new();
                
                // Filter out unavailable/hidden mods
                mods = mods.Where(m => m.Available && !string.IsNullOrEmpty(m.Name)).ToList();

                return new NexusSearchResult
                {
                    Success = true,
                    Mods = mods,
                    TotalCount = mods.Count
                };
            }
            else
            {
                return new NexusSearchResult { Success = false, Error = $"API error: {response.StatusCode}" };
            }
        }
        catch (Exception ex)
        {
            return new NexusSearchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get latest updated mods
    /// </summary>
    public async Task<NexusSearchResult> GetUpdatedModsAsync(string gameDomain)
    {
        if (!IsAuthenticated)
            return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/latest_updated.json");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var mods = JsonConvert.DeserializeObject<List<NexusMod>>(json) ?? new();
                
                // Filter out unavailable/hidden mods
                mods = mods.Where(m => m.Available && !string.IsNullOrEmpty(m.Name)).ToList();

                return new NexusSearchResult
                {
                    Success = true,
                    Mods = mods,
                    TotalCount = mods.Count
                };
            }
            else
            {
                return new NexusSearchResult { Success = false, Error = $"API error: {response.StatusCode}" };
            }
        }
        catch (Exception ex)
        {
            return new NexusSearchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get trending mods
    /// </summary>
    public async Task<NexusSearchResult> GetTrendingModsAsync(string gameDomain)
    {
        if (!IsAuthenticated)
            return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/trending.json");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var mods = JsonConvert.DeserializeObject<List<NexusMod>>(json) ?? new();
                
                // Filter out unavailable/hidden mods
                mods = mods.Where(m => m.Available && !string.IsNullOrEmpty(m.Name)).ToList();

                return new NexusSearchResult
                {
                    Success = true,
                    Mods = mods,
                    TotalCount = mods.Count
                };
            }
            else
            {
                return new NexusSearchResult { Success = false, Error = $"API error: {response.StatusCode}" };
            }
        }
        catch (Exception ex)
        {
            return new NexusSearchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get all mods combined from multiple endpoints (latest + trending + updated + monthly)
    /// This provides more results since each endpoint returns max 10
    /// </summary>
    public async Task<NexusSearchResult> GetAllModsCombinedAsync(string gameDomain)
    {
        if (!IsAuthenticated)
            return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        try
        {
            // Fetch from multiple endpoints in parallel
            var latestTask = GetLatestModsAsync(gameDomain);
            var trendingTask = GetTrendingModsAsync(gameDomain);
            var updatedTask = GetUpdatedModsAsync(gameDomain);
            var monthlyTask = GetUpdatedModsWithPeriodAsync(gameDomain, "1m"); // Last month

            await Task.WhenAll(latestTask, trendingTask, updatedTask, monthlyTask);

            var allMods = new List<NexusMod>();

            if (latestTask.Result.Success)
                allMods.AddRange(latestTask.Result.Mods);
            if (trendingTask.Result.Success)
                allMods.AddRange(trendingTask.Result.Mods);
            if (updatedTask.Result.Success)
                allMods.AddRange(updatedTask.Result.Mods);
            if (monthlyTask.Result.Success)
                allMods.AddRange(monthlyTask.Result.Mods);

            // Remove duplicates and filter unavailable/hidden mods
            var uniqueMods = allMods
                .Where(m => m.Available && !string.IsNullOrEmpty(m.Name)) // Filter hidden/deleted
                .GroupBy(m => m.ModId)
                .Select(g => g.First())
                .OrderByDescending(m => m.Downloads)
                .ToList();

            return new NexusSearchResult
            {
                Success = true,
                Mods = uniqueMods,
                TotalCount = uniqueMods.Count
            };
        }
        catch (Exception ex)
        {
            return new NexusSearchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get mods updated within a specific period (1d, 1w, 1m)
    /// This returns mod IDs only, need to fetch full details separately
    /// </summary>
    public async Task<NexusSearchResult> GetUpdatedModsWithPeriodAsync(string gameDomain, string period = "1m")
    {
        if (!IsAuthenticated)
            return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/updated.json?period={period}");

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var updates = JsonConvert.DeserializeObject<List<NexusModUpdate>>(json) ?? new();

                // Fetch full mod details for each (limited to first 20 to avoid rate limits)
                var mods = new List<NexusMod>();
                var modIds = updates.Take(20).Select(u => u.ModId).Distinct().ToList();
                
                foreach (var modId in modIds)
                {
                    var mod = await GetModAsync(gameDomain, modId);
                    if (mod != null && mod.Available && !string.IsNullOrEmpty(mod.Name))
                    {
                        mods.Add(mod);
                    }
                }

                return new NexusSearchResult
                {
                    Success = true,
                    Mods = mods,
                    TotalCount = mods.Count
                };
            }
            else
            {
                return new NexusSearchResult { Success = false, Error = $"API error: {response.StatusCode}" };
            }
        }
        catch (Exception ex)
        {
            return new NexusSearchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Search for mods - fetches from multiple endpoints and filters
    /// </summary>
    public async Task<NexusSearchResult> SearchModsAsync(string gameDomain, string query, int page = 1)
    {
        if (!IsAuthenticated)
            return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        // If no query, return latest mods
        if (string.IsNullOrWhiteSpace(query))
        {
            return await GetLatestModsAsync(gameDomain);
        }

        try
        {
            // Fetch from multiple sources and combine
            var allMods = new List<NexusMod>();
            
            var latestTask = GetLatestModsAsync(gameDomain);
            var updatedTask = GetUpdatedModsAsync(gameDomain);
            var trendingTask = GetTrendingModsAsync(gameDomain);

            await Task.WhenAll(latestTask, updatedTask, trendingTask);

            if (latestTask.Result.Success)
                allMods.AddRange(latestTask.Result.Mods);
            if (updatedTask.Result.Success)
                allMods.AddRange(updatedTask.Result.Mods);
            if (trendingTask.Result.Success)
                allMods.AddRange(trendingTask.Result.Mods);

            // Remove duplicates and filter by query
            var filteredMods = allMods
                .GroupBy(m => m.ModId)
                .Select(g => g.First())
                .Where(m =>
                    m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (m.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (m.Author?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(m => m.Downloads)
                .ToList();

            return new NexusSearchResult
            {
                Success = true,
                Mods = filteredMods,
                TotalCount = filteredMods.Count
            };
        }
        catch (Exception ex)
        {
            return new NexusSearchResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Get details for a specific mod
    /// </summary>
    public async Task<NexusMod?> GetModAsync(string gameDomain, int modId)
    {
        if (!IsAuthenticated) return null;

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/{modId}.json");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<NexusMod>(json);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Get available files for a mod
    /// </summary>
    public async Task<List<NexusModFile>> GetModFilesAsync(string gameDomain, int modId)
    {
        if (!IsAuthenticated) return new();

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/{modId}/files.json");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<NexusFilesResponse>(json);
                return result?.Files ?? new();
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Get download links for a file (requires Premium for direct links)
    /// </summary>
    public async Task<List<NexusDownloadLink>> GetDownloadLinksAsync(string gameDomain, int modId, int fileId)
    {
        if (!IsAuthenticated) return new();

        try
        {
            var response = await _http.GetAsync($"games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<NexusDownloadLink>>(json) ?? new();
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // Non-premium users can't get direct download links
                // They need to use nxm:// protocol or manual download
                return new();
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Generate download link from nxm:// protocol parameters
    /// nxm://gameDomain/mods/modId/files/fileId?key=xxx&expires=xxx&user_id=xxx
    /// </summary>
    public async Task<List<NexusDownloadLink>> GetDownloadLinksFromNxmAsync(
        string gameDomain, int modId, int fileId, string key, long expires, int userId)
    {
        if (!IsAuthenticated) return new();

        try
        {
            var url = $"games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json" +
                      $"?key={key}&expires={expires}&user_id={userId}";
            
            var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<NexusDownloadLink>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Get list of games on Nexus
    /// </summary>
    public async Task<List<NexusGame>> GetGamesAsync()
    {
        if (!IsAuthenticated) return new();

        try
        {
            var response = await _http.GetAsync("games.json");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<NexusGame>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    /// <summary>
    /// Search for games on Nexus by name
    /// </summary>
    public async Task<List<NexusGame>> SearchGamesAsync(string query)
    {
        var allGames = await GetGamesAsync();
        
        if (string.IsNullOrWhiteSpace(query))
            return allGames.Where(g => g.ModCount > 0).OrderByDescending(g => g.ModCount).Take(50).ToList();
        
        return allGames
            .Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        g.DomainName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(g => g.ModCount)
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// Try to find a Nexus game by matching the local game name
    /// </summary>
    public async Task<NexusGame?> FindGameByNameAsync(string gameName)
    {
        // First check known mappings
        var knownDomain = GetKnownGameDomain(gameName);
        if (knownDomain != null)
        {
            var games = await GetGamesAsync();
            return games.FirstOrDefault(g => g.DomainName.Equals(knownDomain, StringComparison.OrdinalIgnoreCase));
        }
        
        // Otherwise search
        var results = await SearchGamesAsync(gameName);
        return results.FirstOrDefault();
    }

    /// <summary>
    /// Known RPG Maker games and their Nexus domains
    /// </summary>
    public static readonly Dictionary<string, string> KnownRpgMakerGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fear & Hunger series - all variations
        { "Fear & Hunger", "fearandhunger" },
        { "Fear and Hunger", "fearandhunger" },
        { "FearHunger", "fearandhunger" },
        { "FearandHunger", "fearandhunger" },
        { "Fear_and_Hunger", "fearandhunger" },
        
        // Fear & Hunger 2 - all variations
        { "Fear & Hunger 2: Termina", "fearandhunger2termina" },
        { "Fear and Hunger 2: Termina", "fearandhunger2termina" },
        { "Fear & Hunger 2 Termina", "fearandhunger2termina" },
        { "Fear and Hunger 2 Termina", "fearandhunger2termina" },
        { "Fear and Hunger 2", "fearandhunger2termina" },
        { "Fear & Hunger 2", "fearandhunger2termina" },
        { "FearandHunger2Termina", "fearandhunger2termina" },
        { "Termina", "fearandhunger2termina" },
        { "FH2", "fearandhunger2termina" },
        
        // Look Outside
        { "Look Outside", "lookoutside" },
        { "LookOutside", "lookoutside" },
        
        // Popular RPG Maker games
        { "Omori", "omori" },
        { "OMORI", "omori" },
        { "OneShot", "oneshot" },
        { "One Shot", "oneshot" },
        { "ONESHOT", "oneshot" },
        { "To the Moon", "tothemoon" },
        { "ToTheMoon", "tothemoon" },
        { "Lisa: The Painful", "lisathepainful" },
        { "Lisa The Painful", "lisathepainful" },
        { "LISA", "lisathepainful" },
        { "LISA: The Painful", "lisathepainful" },
        { "Ib", "ib" },
        { "IB", "ib" },
        { "Yume Nikki", "yumenikki" },
        { "YumeNikki", "yumenikki" },
        { "RPG Maker MV", "rpgmakermv" },
        { "RPG Maker MZ", "rpgmakermz" },
        { "Corpse Party", "corpseparty" },
        { "CorpseParty", "corpseparty" },
        { "Mad Father", "madfather" },
        { "MadFather", "madfather" },
        { "The Witch's House", "thewitchshouse" },
        { "Witch's House", "thewitchshouse" },
        { "Ao Oni", "aooni" },
        { "AoOni", "aooni" },
        { "OFF", "off" },
        { "Space Funeral", "spacefuneral" },
        { "Jimmy and the Pulsating Mass", "jimmyandthepulsatingmass" },
        { "HEARTBEAT", "heartbeat" },
        { "Heartbeat", "heartbeat" },
        { "Escaped Chasm", "escapedchasm" },
        { "DELTARUNE", "deltarune" },
        { "Deltarune", "deltarune" },
        { "Undertale", "undertale" },
        { "UNDERTALE", "undertale" },
    };

    /// <summary>
    /// Try to get a known Nexus domain for a game name
    /// </summary>
    public static string? GetKnownGameDomain(string gameName)
    {
        // Direct lookup
        if (KnownRpgMakerGames.TryGetValue(gameName, out var domain))
            return domain;
        
        // Fuzzy match - check if any known game name is contained in the input
        foreach (var kvp in KnownRpgMakerGames)
        {
            if (gameName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains(gameName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        
        return null;
    }

    /// <summary>
    /// Check for mod updates by comparing versions
    /// </summary>
    public async Task<NexusUpdateCheck> CheckForUpdatesAsync(string gameDomain, int modId, string currentVersion)
    {
        var mod = await GetModAsync(gameDomain, modId);
        if (mod == null)
            return new NexusUpdateCheck { Success = false, Error = "Could not fetch mod info" };

        bool hasUpdate = !string.Equals(mod.Version, currentVersion, StringComparison.OrdinalIgnoreCase);

        return new NexusUpdateCheck
        {
            Success = true,
            HasUpdate = hasUpdate,
            CurrentVersion = currentVersion,
            LatestVersion = mod.Version,
            Mod = mod
        };
    }

    /// <summary>
    /// Download a file from a URL with progress reporting
    /// </summary>
    public async Task<string?> DownloadFileAsync(string downloadUrl, string destinationFolder, IProgress<double>? progress = null)
    {
        try
        {
            Directory.CreateDirectory(destinationFolder);

            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Get filename from Content-Disposition header or URL
            string fileName = "download.zip";
            if (response.Content.Headers.ContentDisposition?.FileName != null)
            {
                fileName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
            }
            else
            {
                var uri = new Uri(downloadUrl);
                fileName = Path.GetFileName(uri.LocalPath);
                if (string.IsNullOrEmpty(fileName))
                    fileName = "download.zip";
            }

            string filePath = Path.Combine(destinationFolder, fileName);
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var downloadedBytes = 0L;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                downloadedBytes += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)downloadedBytes / totalBytes * 100);
                }
            }

            return filePath;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

#region API Response Models

public class NexusAuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public NexusUser? User { get; set; }
}

public class NexusUser
{
    [JsonProperty("user_id")]
    public int UserId { get; set; }

    [JsonProperty("key")]
    public string Key { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("is_premium")]
    public bool IsPremium { get; set; }

    [JsonProperty("is_supporter")]
    public bool IsSupporter { get; set; }

    [JsonProperty("email")]
    public string Email { get; set; } = "";

    [JsonProperty("profile_url")]
    public string ProfileUrl { get; set; } = "";
}

public class NexusSearchResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<NexusMod> Mods { get; set; } = new();
    public int TotalCount { get; set; }
}

public class NexusMod
{
    [JsonProperty("mod_id")]
    public int ModId { get; set; }

    [JsonProperty("game_id")]
    public int GameId { get; set; }

    [JsonProperty("domain_name")]
    public string DomainName { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("summary")]
    public string? Summary { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("version")]
    public string Version { get; set; } = "";

    [JsonProperty("author")]
    public string Author { get; set; } = "";

    [JsonProperty("uploaded_by")]
    public string UploadedBy { get; set; } = "";
    
    // Nested user object that some endpoints return
    [JsonProperty("user")]
    public NexusModUser? User { get; set; }

    [JsonProperty("picture_url")]
    public string? PictureUrl { get; set; }

    [JsonProperty("mod_downloads")]
    public int Downloads { get; set; }

    [JsonProperty("mod_unique_downloads")]
    public int UniqueDownloads { get; set; }

    [JsonProperty("endorsement_count")]
    public int Endorsements { get; set; }

    [JsonProperty("created_timestamp")]
    public long CreatedTimestamp { get; set; }

    [JsonProperty("updated_timestamp")]
    public long UpdatedTimestamp { get; set; }

    [JsonProperty("available")]
    public bool Available { get; set; }

    // Convenience properties
    public DateTime CreatedDate => CreatedTimestamp > 0 
        ? DateTimeOffset.FromUnixTimeSeconds(CreatedTimestamp).DateTime 
        : DateTime.MinValue;
    public DateTime UpdatedDate => UpdatedTimestamp > 0 
        ? DateTimeOffset.FromUnixTimeSeconds(UpdatedTimestamp).DateTime 
        : DateTime.MinValue;
    public string DownloadsFormatted => Downloads.ToString("N0") + " downloads";
    
    // Get author from either author field or nested user object
    public string AuthorFormatted => !string.IsNullOrEmpty(Author) 
        ? $"by {Author}" 
        : (!string.IsNullOrEmpty(UploadedBy) ? $"by {UploadedBy}" 
            : (User != null ? $"by {User.Name}" : ""));
    public string VersionFormatted => !string.IsNullOrEmpty(Version) ? $"v{Version}" : "";
}

public class NexusModUser
{
    [JsonProperty("member_id")]
    public int MemberId { get; set; }
    
    [JsonProperty("member_group_id")]
    public int MemberGroupId { get; set; }
    
    [JsonProperty("name")]
    public string Name { get; set; } = "";
}

public class NexusFilesResponse
{
    [JsonProperty("files")]
    public List<NexusModFile> Files { get; set; } = new();
}

public class NexusModFile
{
    [JsonProperty("file_id")]
    public int FileId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("version")]
    public string Version { get; set; } = "";

    [JsonProperty("category_id")]
    public int CategoryId { get; set; }

    [JsonProperty("category_name")]
    public string? CategoryName { get; set; }

    [JsonProperty("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonProperty("file_name")]
    public string FileName { get; set; } = "";

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("size_kb")]
    public long SizeKb { get; set; }

    [JsonProperty("uploaded_timestamp")]
    public long UploadedTimestamp { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    // Convenience
    public string SizeFormatted => SizeKb < 1024 
        ? $"{SizeKb} KB" 
        : $"{SizeKb / 1024.0:F1} MB";

    public DateTime UploadedDate => DateTimeOffset.FromUnixTimeSeconds(UploadedTimestamp).DateTime;
}

public class NexusDownloadLink
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("short_name")]
    public string ShortName { get; set; } = "";

    [JsonProperty("URI")]
    public string Uri { get; set; } = "";
}

public class NexusGame
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("forum_url")]
    public string ForumUrl { get; set; } = "";

    [JsonProperty("nexusmods_url")]
    public string NexusmodsUrl { get; set; } = "";

    [JsonProperty("genre")]
    public string Genre { get; set; } = "";

    [JsonProperty("domain_name")]
    public string DomainName { get; set; } = "";

    [JsonProperty("mods")]
    public int ModCount { get; set; }

    [JsonProperty("downloads")]
    public long Downloads { get; set; }
    
    // Formatted properties for XAML binding
    public string DomainNameFormatted => $"({DomainName})";
    public string ModCountFormatted => $"{ModCount} mods";
}

public class NexusUpdateCheck
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public NexusMod? Mod { get; set; }
}

/// <summary>
/// Response from the updated.json endpoint (returns mod IDs and timestamps, not full mod data)
/// </summary>
public class NexusModUpdate
{
    [JsonProperty("mod_id")]
    public int ModId { get; set; }
    
    [JsonProperty("latest_file_update")]
    public long LatestFileUpdate { get; set; }
    
    [JsonProperty("latest_mod_activity")]
    public long LatestModActivity { get; set; }
}

#endregion
