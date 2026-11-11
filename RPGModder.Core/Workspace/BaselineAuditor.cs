using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RPGModder.Core.Workspace;

/// <summary>
/// The baseline audit (TX-21): compares candidate vanilla files with the files of every installed
/// mod, whatever their names. A candidate matches a mod file when their SHA-256 is equal or, for
/// JSON, when they are equal after normalization; the match is <c>same_content_and_time</c> when
/// the last-write times are also equal to the second. A candidate is hashed only when its size
/// equals a mod file's size, or when it is JSON and a mod ships JSON. The audit never changes a file.
/// </summary>
public sealed class BaselineAuditor
{
    private readonly string _modsRoot;

    public BaselineAuditor(string modsRoot)
    {
        _modsRoot = modsRoot;
    }

    public BaselineAudit Audit(string root, IEnumerable<string> relativeFiles, DateTimeOffset utcNow)
    {
        ModIndex index = IndexMods();
        var audit = new BaselineAudit { AuditedUtc = utcNow };
        var perMod = new SortedDictionary<string, BaselineAuditMod>(StringComparer.Ordinal);

        foreach (string relative in relativeFiles)
        {
            string path = Path.Combine(root, relative);
            bool json = IsJson(path);
            if (!index.Sizes.Contains(new FileInfo(path).Length) && !(json && index.ByJson.Count > 0))
            {
                continue;
            }

            ContentKeys keys = ContentKeys.Of(path, json);
            List<ModFile> matches = index.Matches(keys);
            if (matches.Count == 0)
            {
                continue;
            }

            long seconds = Seconds(File.GetLastWriteTimeUtc(path));
            bool timeMatch = false;
            foreach (IGrouping<string, ModFile> mod in matches.GroupBy(file => file.Mod))
            {
                bool modTime = mod.Any(file => file.Seconds == seconds);
                timeMatch |= modTime;
                if (!perMod.TryGetValue(mod.Key, out BaselineAuditMod? counts))
                {
                    perMod[mod.Key] = counts = new BaselineAuditMod { Mod = mod.Key };
                }

                if (modTime)
                {
                    counts.SameContentAndTime++;
                }
                else
                {
                    counts.SameContent++;
                }
            }

            if (timeMatch)
            {
                audit.SameContentAndTime++;
            }
            else
            {
                audit.SameContent++;
            }
        }

        audit.Mods = perMod.Values.ToList();
        return audit;
    }

    private ModIndex IndexMods()
    {
        var index = new ModIndex();
        if (!Directory.Exists(_modsRoot))
        {
            return index;
        }

        foreach (string folder in Directory.GetDirectories(_modsRoot))
        {
            if (!File.Exists(Path.Combine(folder, ModStore.ManifestFileName)))
            {
                continue;
            }

            string mod = Path.GetFileName(folder);
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (Path.GetDirectoryName(file) == folder && Path.GetFileName(file).Equals(ModStore.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                index.Add(new ModFile(mod, ContentKeys.Of(file, IsJson(file)), Seconds(File.GetLastWriteTimeUtc(file))), new FileInfo(file).Length);
            }
        }

        return index;
    }

    private static bool IsJson(string path)
    {
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static long Seconds(DateTime utc)
    {
        return utc.Ticks / TimeSpan.TicksPerSecond;
    }

    private sealed record ModFile(string Mod, ContentKeys Keys, long Seconds);

    private sealed class ModIndex
    {
        public HashSet<long> Sizes { get; } = new();
        public Dictionary<string, List<ModFile>> BySha { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ModFile>> ByJson { get; } = new(StringComparer.Ordinal);

        public void Add(ModFile file, long size)
        {
            Sizes.Add(size);
            Append(BySha, file.Keys.Sha256, file);
            if (file.Keys.NormalizedJson != null)
            {
                Append(ByJson, file.Keys.NormalizedJson, file);
            }
        }

        public List<ModFile> Matches(ContentKeys keys)
        {
            var matches = new List<ModFile>();
            if (BySha.TryGetValue(keys.Sha256, out List<ModFile>? bySha))
            {
                matches.AddRange(bySha);
            }

            if (keys.NormalizedJson != null && ByJson.TryGetValue(keys.NormalizedJson, out List<ModFile>? byJson))
            {
                matches.AddRange(byJson.Where(file => !matches.Contains(file)));
            }

            return matches;
        }

        private static void Append(Dictionary<string, List<ModFile>> map, string key, ModFile file)
        {
            if (!map.TryGetValue(key, out List<ModFile>? list))
            {
                map[key] = list = new List<ModFile>();
            }

            list.Add(file);
        }
    }

    private sealed record ContentKeys(string Sha256, string? NormalizedJson)
    {
        public static ContentKeys Of(string path, bool json)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            if (!json)
            {
                return new ContentKeys(sha, null);
            }

            try
            {
                string text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
                string normalized = JToken.Parse(text).ToString(Formatting.None);
                return new ContentKeys(sha, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
            }
            catch (JsonException ex)
            {
                // A file that is not JSON is compared by its bytes alone.
                Trace.TraceInformation($"The audit compares {path} by bytes: {ex.Message}");
                return new ContentKeys(sha, null);
            }
        }
    }
}
