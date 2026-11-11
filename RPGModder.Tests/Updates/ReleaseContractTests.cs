using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Operations;
using RPGModder.Core.Updates;
using System.Reflection;
using System.Text.RegularExpressions;

namespace RPGModder.Tests.Updates;

/// <summary>
/// TST-14 and CI-17: the names the release produces are the names the updaters look for, the
/// 2.0.0 updater still finds a package, diagnostic codes are unique, and the manifest keeps unknown fields.
/// </summary>
public sealed class ReleaseContractTests
{
    private const string Version = "2.1.0";

    public static TheoryData<bool> Platforms => new() { false, true };

    [Theory]
    [MemberData(nameof(Platforms))]
    public void LegacyUpdater_FindsAPackage_InEveryAssetOrder(bool linux)
    {
        IReadOnlyList<string> packages = ReleaseAssets.PackagesFor(linux, Version);
        foreach (IReadOnlyList<string> order in Permutations(ReleaseAssets.All(Version)))
        {
            string? chosen = LegacySelect(order, linux);

            Assert.NotNull(chosen);
            Assert.Contains(chosen, packages);
        }
    }

    [Fact]
    public void AssetNames_KeepTheLegacyTokensApart()
    {
        IReadOnlyList<string> all = ReleaseAssets.All(Version);

        Assert.Equal(new[] { ReleaseAssets.WindowsZip(Version) }, all.Where(name => name.Contains("win", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(
            ReleaseAssets.PackagesFor(linux: true, Version).Order(StringComparer.Ordinal),
            all.Where(name => name.Contains("linux", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("win", ReleaseAssets.Checksums, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("linux", ReleaseAssets.Checksums, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ReleaseWorkflow_ProducesTheAssetNames()
    {
        string workflow = Contract("release.yml");

        foreach (string name in new[] { ReleaseAssets.WindowsZip("$VERSION"), ReleaseAssets.LinuxTarGz("$VERSION"), ReleaseAssets.LinuxZip("$VERSION") })
        {
            Assert.Contains($"\"{name}\"", workflow, StringComparison.Ordinal);
        }

        Assert.Contains($"sha256sum RPGModder-* > {ReleaseAssets.Checksums}", workflow, StringComparison.Ordinal);
        Assert.Contains("--draft", workflow, StringComparison.Ordinal);
        Assert.Contains("\"$TAG\" != \"v$version\"", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageScript_NamesTheLinuxPackages_WithoutAFixedVersion()
    {
        string script = Contract("package.sh");

        Assert.Contains("runtime=\"${RUNTIME:-" + ReleaseAssets.LinuxRuntime + "}\"", script, StringComparison.Ordinal);
        Assert.Contains("package_name=\"" + ReleaseAssets.Product + "-$version-$runtime\"", script, StringComparison.Ordinal);
        Assert.Contains("$package_name.tar.gz", script, StringComparison.Ordinal);
        Assert.Contains("$package_name.zip", script, StringComparison.Ordinal);
        Assert.Contains("version=\"${1:?", script, StringComparison.Ordinal);
        string code = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
        Assert.DoesNotMatch(new Regex(@"\b\d+\.\d+\.\d+\b"), code);
    }

    [Fact]
    public void DiagnosticCodes_AreUniqueAndWellFormed()
    {
        List<string> codes = typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches(@"^[a-z0-9_]+(\.[a-z0-9_]+)+$", code));
    }

    [Fact]
    public void ModManifest_RoundTrip_KeepsUnknownFields()
    {
        const string json = """
            {
              "id": "example.mod",
              "name": "Example",
              "version": "1.0.0",
              "future_field": { "nested": [1, 2, 3] },
              "future_flag": true
            }
            """;

        ModManifest manifest = JsonConvert.DeserializeObject<ModManifest>(json)!;
        JObject written = JObject.Parse(JsonConvert.SerializeObject(manifest));

        Assert.True(JToken.DeepEquals(JToken.Parse("""{ "nested": [1, 2, 3] }"""), written["future_field"]));
        Assert.True(written.Value<bool>("future_flag"));
    }

    /// <summary>
    /// The asset choice of the 2.0.0 updater (tag v2.0.0, <c>UpdateService.CheckForUpdatesAsync</c> and
    /// <c>IsSupportedPackageName</c>), kept as it was: installed copies run it, so it must not change.
    /// </summary>
    private static string? LegacySelect(IEnumerable<string> names, bool linux)
    {
        bool IsSupportedPackageName(string name) => linux
            ? name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        string platformToken = linux ? "linux" : "win";
        return names.FirstOrDefault(name => IsSupportedPackageName(name) && name.Contains(platformToken, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<IReadOnlyList<string>> Permutations(IReadOnlyList<string> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (int index = 0; index < items.Count; index++)
        {
            List<string> rest = items.Where((_, other) => other != index).ToList();
            foreach (IReadOnlyList<string> tail in Permutations(rest))
            {
                yield return [items[index], .. tail];
            }
        }
    }

    private static string Contract(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", name));
}
