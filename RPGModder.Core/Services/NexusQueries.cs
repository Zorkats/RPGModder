using System.Collections.Generic;
using Newtonsoft.Json;

namespace RPGModder.Core.Services;

public static class NexusQueries
{
    // Search query with offset pagination
    public const string SearchModsQuery = @"
        query SearchMods($gameId: String!, $searchTerm: String!, $count: Int!, $offset: Int!) {
            mods(filter: { gameId: [{ value: $gameId, op: EQUALS }], nameStemmed: [{ value: $searchTerm, op: MATCHES }] }, count: $count, offset: $offset) {
                nodes { modId name summary version author pictureUrl status endorsements downloads modCategory { name } }
                nodesCount
                totalCount
            }
        }";


    // Base query for sorting across all categories
    public const string BrowseModsQuery = @"
        query BrowseMods($gameId: String!, $sort: [ModsSort!], $count: Int!, $offset: Int!) {
            mods(filter: { gameId: [{ value: $gameId, op: EQUALS }] }, sort: $sort, count: $count, offset: $offset) {
                nodes { modId name summary version author pictureUrl status endorsements downloads modCategory { name } }
                nodesCount
                totalCount
            }
        }";

    // Isolated query for specific category filtering
    public const string BrowseModsByCategoryQuery = @"
        query BrowseModsByCategory($gameId: String!, $categoryName: String!, $sort: [ModsSort!], $count: Int!, $offset: Int!) {
            mods(filter: { gameId: [{ value: $gameId, op: EQUALS }], categoryName: [{ value: $categoryName, op: EQUALS }] }, sort: $sort, count: $count, offset: $offset) {
                nodes { modId name summary version author pictureUrl status endorsements downloads modCategory { name } }
                nodesCount
                totalCount
            }
        }";
    public const string GameCategoriesQuery = @"
        query GetCategories($gameId: String!) {
            categories(filter: { gameId: [{ value: $gameId, op: EQUALS }] }) {
                nodes {
                    id
                    name
                    parentId
                }
            }
        }";
}

// --- GraphQL Data Transfer Objects (DTOs) ---

public class GqlModResponse
{
    [JsonProperty("mods")]
    public GqlModsConnection Mods { get; set; } = new();
}

public class GqlModsConnection
{
    [JsonProperty("nodes")]
    public List<GqlModNode> Nodes { get; set; } = new();

    [JsonProperty("nodesCount")]
    public int NodesCount { get; set; }

    [JsonProperty("totalCount")]
    public int TotalCount { get; set; }
}

public class GqlModNode
{
    public int ModId { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string PictureUrl { get; set; } = "";
    public string Status { get; set; } = "";
    public int Endorsements { get; set; }
    public int Downloads { get; set; }
}
public class GqlCategoryResponse
{
    [JsonProperty("categories")]
    public GqlCategoriesConnection Categories { get; set; } = new();
}

public class GqlCategoriesConnection
{
    [JsonProperty("nodes")]
    public List<GqlCategoryNode> Nodes { get; set; } = new();
}

public class GqlCategoryNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ParentId { get; set; } // Now strictly an integer as per the V2 docs!
}