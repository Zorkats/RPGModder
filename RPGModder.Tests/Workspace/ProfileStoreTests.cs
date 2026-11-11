using Newtonsoft.Json.Linq;
using RPGModder.Core.Models;
using RPGModder.Core.Workspace;
using RPGModder.Tests.Support;

namespace RPGModder.Tests.Workspace;

public sealed class ProfileStoreTests : IDisposable
{
    // A profile as RPGModder 2.0.0 wrote it: no version field, two lists (GR-V8 fixture).
    private const string Version1Fixture = """
        {
          "EnabledMods": [ "ExampleMod" ],
          "LoadOrder": [ "ExampleMod", "OtherMod" ]
        }
        """;

    private readonly TempDirectory _temp = new();
    private readonly GameWorkspacePaths _workspace;
    private readonly ProfileStore _store = new();

    public ProfileStoreTests()
    {
        _workspace = new GameWorkspacePaths(_temp.Combine("game"));
        _workspace.EnsureCreated();
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Version1Profile_MigratesOnLoad_AndTheFileIsNotRewrittenOnRead()
    {
        string path = WriteProfile(ProfileStore.DefaultProfileName, Version1Fixture);
        byte[] before = File.ReadAllBytes(path);

        ModProfile profile = _store.Load(_workspace, ProfileStore.DefaultProfileName);

        Assert.Equal(2, profile.SchemaVersion);
        Assert.Equal(new[] { "ExampleMod" }, profile.EnabledMods);
        Assert.Equal(new[] { "ExampleMod", "OtherMod" }, profile.LoadOrder);
        Assert.Empty(profile.Resolutions);
        Assert.Null(profile.Rules);
        Assert.True(profile.Options.VerifyAfterWrite);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Version1Profile_IsWrittenAsVersion2OnTheNextSave()
    {
        string path = WriteProfile(ProfileStore.DefaultProfileName, Version1Fixture);
        ModProfile profile = _store.Load(_workspace, ProfileStore.DefaultProfileName);

        _store.Save(_workspace, ProfileStore.DefaultProfileName, profile);

        JObject saved = JObject.Parse(File.ReadAllText(path));
        Assert.Equal(2, saved.Value<int>("SchemaVersion"));
        Assert.Equal(new[] { "ExampleMod" }, saved["EnabledMods"]!.ToObject<string[]>());
    }

    [Fact]
    public void NewerProfile_IsNeverModified()
    {
        string path = WriteProfile("Future", """
            {
              "SchemaVersion": 3,
              "EnabledMods": [ "ExampleMod" ],
              "LoadOrder": [ "ExampleMod" ],
              "FutureField": { "kept": true }
            }
            """);
        byte[] before = File.ReadAllBytes(path);

        ModProfile profile = _store.Load(_workspace, "Future");

        Assert.False(profile.IsSupported);
        Assert.False(_store.CanWrite(_workspace, "Future", profile));
        Assert.False(_store.CanWrite(_workspace, "Future", new ModProfile()));
        Assert.Throws<InvalidDataException>(() => _store.Save(_workspace, "Future", profile));
        Assert.Throws<InvalidDataException>(() => _store.Save(_workspace, "Future", new ModProfile()));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnknownFields_SurviveALoadAndSave()
    {
        string path = WriteProfile(ProfileStore.DefaultProfileName, """
            {
              "SchemaVersion": 2,
              "EnabledMods": [],
              "LoadOrder": [],
              "Resolutions": [ { "CollisionId": "c1", "Kind": "Winner", "ModId": "ExampleMod", "Reason": "kept" } ],
              "Options": { "VerifyAfterWrite": false, "FutureOption": 7 },
              "FutureField": [ 1, 2 ],
              "Note": "2024-01-02T03:04:05Z"
            }
            """);

        ModProfile profile = _store.Load(_workspace, ProfileStore.DefaultProfileName);
        _store.Save(_workspace, ProfileStore.DefaultProfileName, profile);

        JObject saved = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings());
        Assert.Equal(new[] { 1, 2 }, saved["FutureField"]!.ToObject<int[]>());
        Assert.Equal(7, saved["Options"]!.Value<int>("FutureOption"));
        Assert.False(saved["Options"]!.Value<bool>("VerifyAfterWrite"));
        Assert.Equal("kept", saved["Resolutions"]![0]!.Value<string>("Reason"));
        Assert.Contains("\"Note\": \"2024-01-02T03:04:05Z\"", File.ReadAllText(path));
    }

    [Fact]
    public void FailedSave_LeavesThePreviousProfileWhole()
    {
        string path = WriteProfile(ProfileStore.DefaultProfileName, Version1Fixture);
        byte[] before = File.ReadAllBytes(path);
        var store = new ProfileStore(new FaultInjectingFileOps(failAt: 1));

        Assert.Throws<IOException>(() => store.Save(_workspace, ProfileStore.DefaultProfileName, new ModProfile()));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private string WriteProfile(string profileName, string json)
    {
        string path = _workspace.GetProfilePath(profileName);
        File.WriteAllText(path, json);
        return path;
    }
}
