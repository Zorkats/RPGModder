using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RPGModder.Core.Models;

public class NexusAuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public NexusUser? User { get; set; }
}

public class NexusUser
{
    [JsonProperty("user_id")] public int UserId { get; set; }
    [JsonProperty("key")] public string Key { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("is_premium")] public bool IsPremium { get; set; }
    [JsonProperty("is_supporter")] public bool IsSupporter { get; set; }
    [JsonProperty("email")] public string Email { get; set; } = "";
    [JsonProperty("profile_url")] public string ProfileUrl { get; set; } = "";
}

public class NexusSearchResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<NexusMod> Mods { get; set; } = new();
    public int TotalCount { get; set; }
    public bool IsClientSideSearch { get; set; }

    public int NextOffset { get; set; }
    public bool HasNextPage { get; set; }
}

public class NexusMod
{
    [JsonProperty("mod_id")] public int ModId { get; set; }
    [JsonProperty("game_id")] public int GameId { get; set; }
    [JsonProperty("domain_name")] public string DomainName { get; set; } = "";
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("summary")] public string? Summary { get; set; }
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("author")] public string Author { get; set; } = "";
    [JsonProperty("uploaded_by")] public string UploadedBy { get; set; } = "";
    [JsonProperty("user")] public NexusModUser? User { get; set; }
    [JsonProperty("picture_url")] public string? PictureUrl { get; set; }
    [JsonProperty("mod_downloads")] public int Downloads { get; set; }
    [JsonProperty("mod_unique_downloads")] public int UniqueDownloads { get; set; }
    [JsonProperty("endorsement_count")] public int EndorsementCount { get; set; }
    [JsonProperty("created_timestamp")] public long CreatedTimestamp { get; set; }
    [JsonProperty("updated_timestamp")] public long UpdatedTimestamp { get; set; }
    [JsonProperty("available")] public bool Available { get; set; }

    public DateTime CreatedDate => CreatedTimestamp > 0
        ? DateTimeOffset.FromUnixTimeSeconds(CreatedTimestamp).DateTime
        : DateTime.MinValue;
    public DateTime UpdatedDate => UpdatedTimestamp > 0
        ? DateTimeOffset.FromUnixTimeSeconds(UpdatedTimestamp).DateTime
        : DateTime.MinValue;
    public string DownloadsFormatted => Downloads.ToString("N0") + " downloads";

    public string AuthorFormatted => !string.IsNullOrEmpty(Author)
        ? $"by {Author}"
        : (!string.IsNullOrEmpty(UploadedBy) ? $"by {UploadedBy}"
            : (User != null ? $"by {User.Name}" : ""));
    public string VersionFormatted => !string.IsNullOrEmpty(Version) ? $"v{Version}" : "";
}

public class NexusModUser
{
    [JsonProperty("member_id")] public int MemberId { get; set; }
    [JsonProperty("member_group_id")] public int MemberGroupId { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
}

public class NexusFilesResponse
{
    [JsonProperty("files")] public List<NexusModFile> Files { get; set; } = new();
}

public class NexusModFile
{
    [JsonProperty("file_id")] public int FileId { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("category_id")] public int CategoryId { get; set; }
    [JsonProperty("category_name")] public string? CategoryName { get; set; }
    [JsonProperty("is_primary")] public bool IsPrimary { get; set; }
    [JsonProperty("file_name")] public string FileName { get; set; } = "";
    [JsonProperty("size")] public long Size { get; set; }
    [JsonProperty("size_kb")] public long SizeKb { get; set; }
    [JsonProperty("uploaded_timestamp")] public long UploadedTimestamp { get; set; }
    [JsonProperty("description")] public string? Description { get; set; }

    public string SizeFormatted => SizeKb < 1024
        ? $"{SizeKb} KB"
        : $"{SizeKb / 1024.0:F1} MB";
    public DateTime UploadedDate => DateTimeOffset.FromUnixTimeSeconds(UploadedTimestamp).DateTime;
}

public class NexusDownloadLink
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("short_name")] public string ShortName { get; set; } = "";
    [JsonProperty("URI")] public string Uri { get; set; } = "";
}

public class NexusGame
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("forum_url")] public string ForumUrl { get; set; } = "";
    [JsonProperty("nexusmods_url")] public string NexusmodsUrl { get; set; } = "";
    [JsonProperty("genre")] public string Genre { get; set; } = "";
    [JsonProperty("domain_name")] public string DomainName { get; set; } = "";
    [JsonProperty("mods")] public int ModCount { get; set; }
    [JsonProperty("downloads")] public long Downloads { get; set; }

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

public class NexusModUpdate
{
    [JsonProperty("mod_id")] public int ModId { get; set; }
    [JsonProperty("latest_file_update")] public long LatestFileUpdate { get; set; }
    [JsonProperty("latest_mod_activity")] public long LatestModActivity { get; set; }
}

public class NexusCategory
{
    [JsonProperty("category_id")] public int CategoryId { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";

    public override string ToString() => Name; // Ensures the ComboBox renders the text correctly
}