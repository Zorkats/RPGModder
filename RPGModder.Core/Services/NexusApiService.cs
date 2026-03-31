using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;

namespace RPGModder.Core.Services;

public class NexusApiService : IDisposable
{
    private readonly HttpClient _v1Client;
    private readonly GraphQLHttpClient _graphClient;
    private string _apiKey = "";
    public bool IsAuthenticated => !string.IsNullOrEmpty(_apiKey);

    public void ClearAuth()
    {
        _apiKey = "";
        _v1Client.DefaultRequestHeaders.Remove("apikey");
        _graphClient.HttpClient.DefaultRequestHeaders.Remove("apikey");
    }

    public NexusApiService()
    {
        _v1Client = new HttpClient();
        _v1Client.BaseAddress = new Uri("https://api.nexusmods.com/v1/");
        _v1Client.DefaultRequestHeaders.Add("Application-Name", "RPGModder");
        _v1Client.DefaultRequestHeaders.Add("Application-Version", "1.0.0");

        _graphClient = new GraphQLHttpClient("https://api.nexusmods.com/v2/graphql", new NewtonsoftJsonSerializer());
        _graphClient.HttpClient.DefaultRequestHeaders.Add("Application-Name", "RPGModder");
        _graphClient.HttpClient.DefaultRequestHeaders.Add("Application-Version", "1.0.0");
    }

    public async Task<NexusAuthResult> AuthenticateAsync(string apiKey)
    {
        try
        {
            _v1Client.DefaultRequestHeaders.Remove("apikey");
            _v1Client.DefaultRequestHeaders.Add("apikey", apiKey);
            var response = await _v1Client.GetAsync("users/validate.json");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var user = JsonConvert.DeserializeObject<NexusUser>(content);
                if (user != null)
                {
                    _apiKey = apiKey;
                    _graphClient.HttpClient.DefaultRequestHeaders.Remove("apikey");
                    _graphClient.HttpClient.DefaultRequestHeaders.Add("apikey", apiKey);
                    return new NexusAuthResult { Success = true, User = user };
                }
            }
            return new NexusAuthResult { Success = false, Error = "Invalid API Key." };
        }
        catch (Exception ex) { return new NexusAuthResult { Success = false, Error = ex.Message }; }
    }

    // ========================================================================
    // PIPELINE 2: DYNAMIC GRAPHQL (V2)
    // ========================================================================

    public async Task<NexusSearchResult> SearchModsAsync(string gameDomain, string query, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return await BrowseModsAsync(gameDomain, "Latest", null, offset, ct);

        var game = await FindGameByNameAsync(gameDomain, ct);
        if (game == null) return new NexusSearchResult { Success = false, Error = "Game not found in database." };

        var request = new GraphQLRequest
        {
            Query = NexusQueries.SearchModsQuery,
            Variables = new { gameId = game.Id.ToString(), searchTerm = query, count = 50, offset = offset }
        };

        return await ExecuteGraphQLRequestAsync(request, gameDomain, offset, ct);
    }

    public async Task<NexusSearchResult> BrowseModsAsync(string gameDomain, string sortType, string? categoryName, int offset = 0, CancellationToken ct = default)
    {
        var game = await FindGameByNameAsync(gameDomain, ct);
        if (game == null) return new NexusSearchResult { Success = false, Error = "Game not found in database." };

        object sortObj = sortType switch
        {
            "Latest" => new[] { new { createdAt = new { direction = "DESC" } } },
            "Updated" => new[] { new { updatedAt = new { direction = "DESC" } } },
            "Trending" => new[] { new { endorsements = new { direction = "DESC" } } },
            _ => new[] { new { endorsements = new { direction = "DESC" } } }
        };

        GraphQLRequest request;
        if (!string.IsNullOrEmpty(categoryName))
        {
            request = new GraphQLRequest
            {
                Query = NexusQueries.BrowseModsByCategoryQuery,
                Variables = new { gameId = game.Id.ToString(), categoryName = categoryName, sort = sortObj, count = 50, offset = offset }
            };
        }
        else
        {
            request = new GraphQLRequest
            {
                Query = NexusQueries.BrowseModsQuery,
                Variables = new { gameId = game.Id.ToString(), sort = sortObj, count = 50, offset = offset }
            };
        }

        return await ExecuteGraphQLRequestAsync(request, gameDomain, offset, ct);
    }

    private async Task<NexusSearchResult> ExecuteGraphQLRequestAsync(GraphQLRequest request, string gameDomain, int currentOffset, CancellationToken ct)
    {
        if (!IsAuthenticated) return new NexusSearchResult { Success = false, Error = "Not authenticated" };

        try
        {
            var response = await _graphClient.SendQueryAsync<GqlModResponse>(request, ct);

            if (response.Errors != null && response.Errors.Any())
                return new NexusSearchResult { Success = false, Error = response.Errors.First().Message };

            if (response.Data?.Mods?.Nodes == null)
                return new NexusSearchResult { Success = true, Mods = new List<NexusMod>(), HasNextPage = false };

            var mappedMods = response.Data.Mods.Nodes.Select(node => new NexusMod
            {
                ModId = node.ModId,
                Name = node.Name ?? "Unknown Mod",
                Summary = node.Summary,
                Version = node.Version,
                Author = node.Author,
                PictureUrl = node.PictureUrl,
                EndorsementCount = node.Endorsements,
                Downloads = node.Downloads,
                DomainName = gameDomain,
                Available = string.Equals(node.Status, "published", StringComparison.OrdinalIgnoreCase)
            }).Where(m => m.Available).ToList();

            return new NexusSearchResult
            {
                Success = true,
                Mods = mappedMods,
                TotalCount = response.Data.Mods.TotalCount,
                NextOffset = currentOffset + response.Data.Mods.NodesCount,
                HasNextPage = (currentOffset + response.Data.Mods.NodesCount) < response.Data.Mods.TotalCount
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new NexusSearchResult { Success = false, Error = $"GraphQL Error: {ex.Message}" }; }
    }

    // ========================================================================
    // PIPELINE 1: REST DOWNLOADS, GAMES, & CATEGORIES (V1)
    // ========================================================================

    public async Task<List<NexusCategory>> GetCategoriesAsync(string gameDomain, CancellationToken ct = default)
    {
        if (!IsAuthenticated) return new();
        try
        {
            // The categories are bundled inside the main game info endpoint!
            var response = await _v1Client.GetAsync($"games/{gameDomain}.json", ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new List<NexusCategory> { new NexusCategory { CategoryId = -1, Name = $"API Error: {response.StatusCode}" } };
            }

            var cats = new List<NexusCategory>();
            var data = JObject.Parse(content);

            // Extract the categories array directly from the game object
            if (data["categories"] is JArray catArray)
            {
                foreach (var item in catArray)
                {
                    cats.Add(new NexusCategory
                    {
                        CategoryId = item["category_id"]?.Value<int>() ?? 0,
                        Name = item["name"]?.ToString() ?? "Unknown Category"
                    });
                }
            }

            return cats.OrderBy(c => c.Name).ToList();
        }
        catch (Exception ex)
        {
            return new List<NexusCategory> { new NexusCategory { CategoryId = -1, Name = $"Crash: {ex.Message}" } };
        }
    }

    public async Task<NexusMod?> GetModAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        if (!IsAuthenticated) return null;
        try
        {
            var response = await _v1Client.GetAsync($"games/{gameDomain}/mods/{modId}.json", ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return JsonConvert.DeserializeObject<NexusMod>(content);
            }
        }
        catch { }
        return null;
    }

    public async Task<List<NexusModFile>> GetModFilesAsync(string gameDomain, int modId, CancellationToken ct = default)
    {
        if (!IsAuthenticated) return new();
        try
        {
            var response = await _v1Client.GetAsync($"games/{gameDomain}/mods/{modId}/files.json", ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                var data = JObject.Parse(content);
                var filesArray = data["files"] as JArray;
                if (filesArray != null) return filesArray.ToObject<List<NexusModFile>>() ?? new();
            }
        }
        catch { }
        return new();
    }

    public async Task<List<NexusDownloadLink>> GetDownloadLinksAsync(string gameDomain, int modId, int fileId, CancellationToken ct = default)
    {
        if (!IsAuthenticated) return new();
        try
        {
            var response = await _v1Client.GetAsync($"games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json", ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return JsonConvert.DeserializeObject<List<NexusDownloadLink>>(content) ?? new();
            }
        }
        catch { }
        return new();
    }

    public async Task<List<NexusDownloadLink>> GetDownloadLinksFromNxmAsync(string gameDomain, int modId, int fileId, string key, long expires, int userId, CancellationToken ct = default)
    {
        if (!IsAuthenticated) return new();
        try
        {
            var response = await _v1Client.GetAsync($"games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json?key={key}&expires={expires}&user_id={userId}", ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return JsonConvert.DeserializeObject<List<NexusDownloadLink>>(content) ?? new();
            }
        }
        catch { }
        return new();
    }

    public async Task<List<NexusGame>> GetGamesAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return new();
        try
        {
            var response = await _v1Client.GetAsync("games.json", ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return JsonConvert.DeserializeObject<List<NexusGame>>(content) ?? new();
            }
        }
        catch { }
        return new();
    }

    public async Task<List<NexusGame>> SearchGamesAsync(string query, CancellationToken ct = default)
    {
        var allGames = await GetGamesAsync(ct);
        if (string.IsNullOrWhiteSpace(query))
            return allGames.Where(g => g.ModCount > 0).OrderByDescending(g => g.ModCount).Take(50).ToList();

        return allGames.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || g.DomainName.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(g => g.ModCount).Take(50).ToList();
    }

    public async Task<NexusGame?> FindGameByNameAsync(string gameDomainOrName, CancellationToken ct = default)
    {
        var games = await GetGamesAsync(ct);
        var exactMatch = games.FirstOrDefault(g => g.DomainName.Equals(gameDomainOrName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null) return exactMatch;

        var knownDomain = GetKnownGameDomain(gameDomainOrName);
        if (knownDomain != null) return games.FirstOrDefault(g => g.DomainName.Equals(knownDomain, StringComparison.OrdinalIgnoreCase));

        var results = await SearchGamesAsync(gameDomainOrName, ct);
        return results.FirstOrDefault();
    }

    public async Task<NexusUpdateCheck> CheckForUpdatesAsync(string gameDomain, int modId, string currentVersion, CancellationToken ct = default)
    {
        var mod = await GetModAsync(gameDomain, modId, ct);
        if (mod == null) return new NexusUpdateCheck { Success = false, Error = "Could not fetch mod info" };
        return new NexusUpdateCheck { Success = true, HasUpdate = !string.Equals(mod.Version, currentVersion, StringComparison.OrdinalIgnoreCase), CurrentVersion = currentVersion, LatestVersion = mod.Version, Mod = mod };
    }

    public static readonly Dictionary<string, string> KnownRpgMakerGames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fear & Hunger", "fearandhunger" }, { "Fear and Hunger", "fearandhunger" },
        { "Fear & Hunger 2: Termina", "fearandhunger2termina" }, { "Fear and Hunger 2: Termina", "fearandhunger2termina" },
        { "Omori", "omori" }, { "OneShot", "oneshot" }, { "Lisa: The Painful", "lisathepainful" },
        { "Ib", "ib" }, { "Yume Nikki", "yumenikki" }, { "RPG Maker MV", "rpgmakermv" }, { "RPG Maker MZ", "rpgmakermz" }
    };

    public static string? GetKnownGameDomain(string gameName)
    {
        if (KnownRpgMakerGames.TryGetValue(gameName, out var domain)) return domain;
        foreach (var kvp in KnownRpgMakerGames) if (gameName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)) return kvp.Value;
        return null;
    }

    public void Dispose()
    {
        _v1Client.Dispose();
        _graphClient.Dispose();
    }
}