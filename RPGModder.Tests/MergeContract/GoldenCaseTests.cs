using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Merge;
using RPGModder.Core.Models;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RPGModder.Tests.MergeContract;

public sealed class GoldenCaseTests
{
    private static readonly JsonSerializerSettings ManifestJsonSettings = new()
    {
        DateParseHandling = DateParseHandling.None
    };

    public static TheoryData<string> GoldenCases()
    {
        string goldenRoot = GetGoldenRoot();
        TheoryData<string> data = new();
        if (!Directory.Exists(goldenRoot))
        {
            throw new DirectoryNotFoundException($"The golden cases were not copied to {goldenRoot}.");
        }

        string[] directories = Directory.GetDirectories(goldenRoot);
        Array.Sort(directories, StringComparer.Ordinal);
        foreach (string dir in directories)
        {
            data.Add(Path.GetFileName(dir));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void GoldenCase_Composed_MatchesExpectedPlanAndFiles(string caseName)
    {
        string caseDir = Path.Combine(GetGoldenRoot(), caseName);
        string vanillaDir = Path.Combine(caseDir, "vanilla");
        string expectedDir = Path.Combine(caseDir, "expected");

        List<string> modDirs = Directory.GetDirectories(caseDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name)
                && !string.Equals(name, "vanilla", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "expected", StringComparison.OrdinalIgnoreCase))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        List<ModSource> modSources = new(modDirs.Count);
        HashSet<string> requestedTargets = new(StringComparer.Ordinal);

        for (int priority = 0; priority < modDirs.Count; priority++)
        {
            string modId = modDirs[priority];
            string modDir = Path.Combine(caseDir, modId);
            string manifestPath = Path.Combine(modDir, "mod.json");
            string manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            ModManifest manifest = JsonConvert.DeserializeObject<ModManifest>(manifestJson, ManifestJsonSettings)
                ?? throw new InvalidOperationException($"Failed to deserialize mod.json for {modId}.");

            List<ModFile> modFiles = new(manifest.FileOps.Count);
            foreach (FileOperation op in manifest.FileOps)
            {
                string sourcePath = Path.Combine(modDir, op.Source.Replace('/', Path.DirectorySeparatorChar));
                (string? text, string sha256) = ReadFilePayload(sourcePath, op.Target);
                modFiles.Add(new ModFile(op.Target, op.Source, text, sha256));
                requestedTargets.Add(MergePath.ToKey(op.Target));
            }

            List<ModPatch> modPatches = new(manifest.JsonPatches.Count);
            foreach (JsonPatch patch in manifest.JsonPatches)
            {
                modPatches.Add(new ModPatch(patch.Target, patch.MergeData));
                requestedTargets.Add(MergePath.ToKey(patch.Target));
            }

            if (manifest.PluginsRegistry.Count > 0)
            {
                requestedTargets.Add(MergePath.ToKey("js/plugins.js"));
            }

            modSources.Add(new ModSource(modId, priority, modFiles, modPatches, manifest.PluginsRegistry));
        }

        Dictionary<string, string> vanillaFilesByKey = new(StringComparer.Ordinal);
        if (Directory.Exists(vanillaDir))
        {
            foreach (string filePath in Directory.GetFiles(vanillaDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(vanillaDir, filePath).Replace('\\', '/');
                string key = MergePath.ToKey(relPath);
                vanillaFilesByKey[key] = filePath;
            }
        }

        List<BaseFile> baseFileList = new();
        foreach (string targetKey in requestedTargets.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (vanillaFilesByKey.TryGetValue(targetKey, out string? fullVanillaPath))
            {
                string relPath = Path.GetRelativePath(vanillaDir, fullVanillaPath).Replace('\\', '/');
                (string? text, string sha256) = ReadFilePayload(fullVanillaPath, relPath);
                baseFileList.Add(new BaseFile(relPath, text, sha256));
            }
        }

        BaseFileSet baseFiles = BaseFileSet.From(baseFileList);

        List<ModChangeSet> changeSets = modSources
            .Select(mod => ChangeSetBuilder.Build(mod, baseFiles))
            .ToList();

        List<Resolution> resolutions = LoadResolutions(Path.Combine(caseDir, "resolutions.json"));

        MergePlan plan = Composer.Compose(changeSets, resolutions, baseFiles);

        string expectedCollisionsPath = Path.Combine(expectedDir, "collisions.json");
        JArray expectedCollisions = JsonConvert.DeserializeObject<JArray>(
            File.ReadAllText(expectedCollisionsPath, Encoding.UTF8),
            ManifestJsonSettings) ?? new JArray();
        JArray actualCollisions = ProjectCollisions(plan.Collisions);
        Assert.True(
            JToken.DeepEquals(expectedCollisions, actualCollisions),
            $"Collisions mismatch in case '{caseName}'.\nExpected: {expectedCollisions.ToString(Formatting.None)}\nActual: {actualCollisions.ToString(Formatting.None)}");

        HashSet<string> resolvedCollisionIds = resolutions
            .Select(r => r.CollisionId)
            .ToHashSet(StringComparer.Ordinal);
        bool hasUnresolvedBlocking = expectedCollisions
            .OfType<JObject>()
            .Any(c => (c["blocking"]?.Value<bool>() ?? false)
                && !resolvedCollisionIds.Contains(c["id"]?.Value<string>() ?? ""));

        string expectedFilesDir = Path.Combine(expectedDir, "files");
        Dictionary<string, byte[]> expectedFiles = new(StringComparer.Ordinal);
        if (Directory.Exists(expectedFilesDir))
        {
            foreach (string filePath in Directory.GetFiles(expectedFilesDir, "*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(expectedFilesDir, filePath).Replace('\\', '/');
                expectedFiles[MergePath.ToKey(relPath)] = File.ReadAllBytes(filePath);
            }
        }

        if (hasUnresolvedBlocking)
        {
            Assert.Empty(expectedFiles);
            Assert.False(plan.IsDeployable);
            Assert.Throws<InvalidOperationException>(() => MergedFileWriter.Write(plan, baseFiles, null));
        }
        else
        {
            Assert.True(plan.IsDeployable);
            IReadOnlyList<MergedFile> writtenFiles = MergedFileWriter.Write(plan, baseFiles, null);

            List<string> expectedKeys = expectedFiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            List<string> actualKeys = writtenFiles.Select(f => f.File).ToList();
            Assert.Equal(expectedKeys, actualKeys);

            foreach (MergedFile mergedFile in writtenFiles)
            {
                byte[] expectedBytes = expectedFiles[mergedFile.File];
                byte[] actualBytes = GetMergedFileBytes(mergedFile, caseDir);
                Assert.Equal(expectedBytes, actualBytes);
            }

            foreach (string vanillaKey in vanillaFilesByKey.Keys)
            {
                if (!expectedFiles.ContainsKey(vanillaKey))
                {
                    Assert.DoesNotContain(writtenFiles, w => w.File == vanillaKey);
                }
            }
        }

        foreach (IReadOnlyList<ModChangeSet> csPerm in Permute(changeSets))
        {
            foreach (IReadOnlyList<Resolution> resPerm in Permute(resolutions))
            {
                MergePlan permPlan = Composer.Compose(csPerm, resPerm, baseFiles);
                Assert.Equal(plan.MergeHash, permPlan.MergeHash);
                AssertSameCollisions(plan.Collisions, permPlan.Collisions);
                AssertSameMergedOps(plan.Ops, permPlan.Ops);
            }
        }
    }

    private static string GetGoldenRoot()
    {
        return Path.Combine(AppContext.BaseDirectory, "Merge", "Golden");
    }

    private static (string? Text, string Sha256) ReadFilePayload(string fullPath, string targetPath)
    {
        byte[] bytes = File.ReadAllBytes(fullPath);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (RpgDataSchema.KindOf(targetPath) == MergeFileKind.Opaque)
        {
            return (null, sha256);
        }

        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        return (text, sha256);
    }

    private static List<Resolution> LoadResolutions(string path)
    {
        if (!File.Exists(path))
        {
            return new List<Resolution>();
        }

        JArray array = JsonConvert.DeserializeObject<JArray>(
            File.ReadAllText(path, Encoding.UTF8),
            ManifestJsonSettings) ?? new JArray();

        List<Resolution> result = new(array.Count);
        foreach (JObject item in array.OfType<JObject>())
        {
            string id = item["id"]?.Value<string>() ?? "";
            string kindText = item["kind"]?.Value<string>() ?? "";
            ResolutionKind kind = Enum.Parse<ResolutionKind>(kindText, ignoreCase: false);
            string? modId = item["mod"]?.Type == JTokenType.Null ? null : item["mod"]?.Value<string>();
            string? fingerprint = item["fingerprint"]?.Type == JTokenType.Null ? null : item["fingerprint"]?.Value<string>();
            result.Add(new Resolution(id, kind, modId, fingerprint));
        }

        return result;
    }

    private static JArray ProjectCollisions(IReadOnlyList<Collision> collisions)
    {
        JArray array = new();
        foreach (Collision c in collisions)
        {
            JObject obj = new()
            {
                ["id"] = c.Id,
                ["kind"] = c.Kind.ToString(),
                ["file"] = c.Key.File,
                ["record"] = c.Key.Record,
                ["path"] = c.Path,
                ["mods"] = new JArray(c.ModIds),
                ["blocking"] = c.Blocking,
                ["allowed"] = new JArray(c.Allowed.Select(a => a.ToString()))
            };
            array.Add(obj);
        }

        return array;
    }

    private static byte[] GetMergedFileBytes(MergedFile mergedFile, string caseDir)
    {
        if (mergedFile.Source == MergedFileSource.Text)
        {
            Assert.NotNull(mergedFile.Text);
            return Encoding.UTF8.GetBytes(mergedFile.Text!);
        }

        if (!string.IsNullOrEmpty(mergedFile.DiskPath) && File.Exists(mergedFile.DiskPath))
        {
            return File.ReadAllBytes(mergedFile.DiskPath);
        }

        Assert.NotNull(mergedFile.ModId);
        Assert.NotNull(mergedFile.SourceFile);
        string sourcePath = Path.Combine(
            caseDir,
            mergedFile.ModId!,
            mergedFile.SourceFile!.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(sourcePath);
    }

    private static void AssertSameCollisions(IReadOnlyList<Collision> expected, IReadOnlyList<Collision> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Collision e = expected[i];
            Collision a = actual[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.Key, a.Key);
            Assert.Equal(e.Path, a.Path);
            Assert.Equal(e.ModIds, a.ModIds);
            Assert.Equal(e.Blocking, a.Blocking);
            Assert.Equal(e.Allowed, a.Allowed);
            Assert.Equal(e.Fingerprint, a.Fingerprint);
        }
    }

    private static void AssertSameMergedOps(IReadOnlyList<MergedOp> expected, IReadOnlyList<MergedOp> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            MergedOp e = expected[i];
            MergedOp a = actual[i];
            Assert.Equal(e.ModIds, a.ModIds);
            Assert.Equal(e.Op.ModId, a.Op.ModId);
            Assert.Equal(e.Op.Key, a.Op.Key);
            Assert.Equal(e.Op.Path, a.Op.Path);
            Assert.Equal(e.Op.Kind, a.Op.Kind);
            Assert.Equal(e.Op.SourceFile, a.Op.SourceFile);
            Assert.True(JToken.DeepEquals(e.Op.Value, a.Op.Value));
        }
    }

    private static IEnumerable<IReadOnlyList<T>> Permute<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items.ToList();
            yield break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            T first = items[i];
            List<T> remaining = new(items.Count - 1);
            for (int j = 0; j < items.Count; j++)
            {
                if (j != i)
                {
                    remaining.Add(items[j]);
                }
            }

            foreach (IReadOnlyList<T> tail in Permute(remaining))
            {
                List<T> combined = new(items.Count) { first };
                combined.AddRange(tail);
                yield return combined;
            }
        }
    }
}
