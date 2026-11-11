using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Platform;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

public sealed class ModStoreTests : IDisposable
{
    private const string ModId = "ExampleMod";

    private readonly TempDirectory _temp = new();
    private readonly string _modsRoot;

    public ModStoreTests()
    {
        _modsRoot = _temp.Combine("library");
        Directory.CreateDirectory(_modsRoot);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Manifest_KeepsDateLikeStringsExactly()
    {
        const string json = """
            {
              "metadata": { "id": "ExampleMod", "name": "Example" },
              "json_patches": [ { "target": "data/System.json", "merge_data": { "note": "2024-01-02T03:04:05.120Z" } } ]
            }
            """;

        ModManifest manifest = ModStore.ParseManifest(json);
        string written = ModStore.SerializeManifest(manifest);

        Assert.Equal("2024-01-02T03:04:05.120Z", manifest.JsonPatches[0].MergeData.Value<string>("note"));
        Assert.Contains("\"note\": \"2024-01-02T03:04:05.120Z\"", written);
    }

    [Fact]
    public void Manifest_KeepsUnknownFieldsAtEveryLevel()
    {
        const string json = """
            {
              "metadata": { "id": "ExampleMod", "future_meta": "m" },
              "file_ops": [ { "source": "files/a.png", "target": "img/a.png", "future_op": 1 } ],
              "json_patches": [ { "target": "data/System.json", "merge_data": {}, "future_patch": true } ],
              "plugins_registry": [ { "name": "ExamplePlugin", "status": true, "future_plugin": "p" } ],
              "future_field": { "nested": [ 1, 2 ] }
            }
            """;

        JObject written = JObject.Parse(ModStore.SerializeManifest(ModStore.ParseManifest(json)));

        Assert.Equal("m", written["metadata"]!.Value<string>("future_meta"));
        Assert.Equal(1, written["file_ops"]![0]!.Value<int>("future_op"));
        Assert.True(written["json_patches"]![0]!.Value<bool>("future_patch"));
        Assert.Equal("p", written["plugins_registry"]![0]!.Value<string>("future_plugin"));
        Assert.Equal(new[] { 1, 2 }, written["future_field"]!["nested"]!.ToObject<int[]>());
    }

    [Fact]
    public void Manifest_WithoutAVersionIsVersion1_AndIsNotMigrated()
    {
        ModManifest manifest = ModStore.ParseManifest("""{ "metadata": { "id": "ExampleMod" } }""");

        Assert.Null(manifest.SchemaVersion);
        Assert.Equal(1, manifest.EffectiveSchemaVersion);
        Assert.True(manifest.IsSupported);
        Assert.DoesNotContain("schema_version", ModStore.SerializeManifest(manifest));
    }

    [Fact]
    public void NewerManifest_IsNeverWritten()
    {
        string folder = new ModBuilder(ModId).BuildInto(_modsRoot);
        string manifestPath = Path.Combine(folder, ModStore.ManifestFileName);
        byte[] before = File.ReadAllBytes(manifestPath);
        var store = new ModStore(_modsRoot, new FileSystemOps());
        ModManifest newer = ModStore.ParseManifest("""{ "schema_version": 3, "metadata": { "id": "ExampleMod" } }""");

        Assert.False(newer.IsSupported);
        Assert.Throws<InvalidDataException>(() => store.WriteManifest(ModId, newer));
        Assert.Throws<InvalidDataException>(() => store.Install(folder, "Other", newer));
        Assert.Equal(before, File.ReadAllBytes(manifestPath));
        Assert.False(Directory.Exists(Path.Combine(_modsRoot, "Other")));
    }

    [Fact]
    public void EmptyManifest_IsReportedAsInvalid()
    {
        Assert.Throws<InvalidDataException>(() => ModStore.ParseManifest("null"));
    }

    [Fact]
    public void Install_FailingAtAnyStep_KeepsThePreviousInstall()
    {
        int calls = CountInstallCalls();
        Assert.True(calls >= 4, $"An install over an existing mod made {calls} file calls.");

        for (int failAt = 1; failAt <= calls; failAt++)
        {
            string library = _temp.Combine($"library-{failAt}");
            InstallVersion(library, "old", new FileSystemOps());
            var faulty = new FaultInjectingFileOps(failAt);

            Exception? error = Record.Exception(() => InstallVersion(library, "new", faulty));

            string installed = File.ReadAllText(Path.Combine(library, ModId, "files", "img", "a.txt"));
            if (error == null)
            {
                // Only the last step, removing the set-aside copy, may fail without failing the install.
                Assert.Equal(calls, failAt);
                Assert.Equal("new", installed);
            }
            else
            {
                Assert.IsType<IOException>(error);
                Assert.Equal("old", installed);
                Assert.Empty(Directory.GetDirectories(library, ".install-*"));
                Assert.Empty(Directory.GetDirectories(library, ".rollback-*"));
            }
        }
    }

    [Fact]
    public void MoveToTrash_StampsTheEntry_AndNeverReusesOne()
    {
        var store = new ModStore(_modsRoot, new FileSystemOps());
        string trash = _temp.Combine("trash");
        var now = new DateTimeOffset(2026, 11, 11, 10, 20, 30, TimeSpan.Zero);

        new ModBuilder(ModId).BuildInto(_modsRoot);
        string first = store.MoveToTrash(ModId, trash, now);
        new ModBuilder(ModId).BuildInto(_modsRoot);
        string second = store.MoveToTrash(ModId, trash, now);

        Assert.Equal(Path.Combine(trash, "ExampleMod-20261111102030"), first);
        Assert.Equal(Path.Combine(trash, "ExampleMod-20261111102030-1"), second);
        Assert.False(Directory.Exists(Path.Combine(_modsRoot, ModId)));
        Assert.Throws<DirectoryNotFoundException>(() => store.MoveToTrash(ModId, trash, now));

        store.PurgeTrashEntry(first);
        Assert.False(Directory.Exists(first));
    }

    private int CountInstallCalls()
    {
        string library = _temp.Combine("library-count");
        InstallVersion(library, "old", new FileSystemOps());
        var counter = new FaultInjectingFileOps();
        InstallVersion(library, "new", counter);
        return counter.Calls;
    }

    private void InstallVersion(string library, string content, IFileSystemOps files)
    {
        string source = _temp.Combine($"source-{Guid.NewGuid():N}");
        string folder = new ModBuilder(ModId).ReplaceFile("img/a.txt", content).BuildInto(source);
        new ModStore(library, files).Install(folder, ModId, ModStore.ReadManifestFile(Path.Combine(folder, ModStore.ManifestFileName)));
    }
}
