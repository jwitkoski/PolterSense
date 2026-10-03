using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using PolterSense.Models;
using PolterSense.Services;

namespace PolterSense.Tests;

[TestFixture]
public class ConfigTests
{
    private string _directory = null!;
    [SetUp] public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "poltersense-config-tests-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private ConfigLoader Loader() => new(_directory, NullLogger<ConfigLoader>.Instance);
    private static Config Scene() => new()
    {
        Name = "Fictional test house",
        Floors = [new() { Id = "ground", Name = "Ground", Bounds = [[0, 0, 0], [10, 10, 3]], Rooms = [new() { Id = "room", Name = "Room", Points = [[0, 0], [5, 0], [5, 5], [0, 5]] }] }],
        Wisps = [new() { Id = "wisp", Name = "Receiver display name", ExternalId = "firmware-id", Point = [1, 1, 1], Floors = ["ground"] }],
        Sources = [new() { Id = "source", Name = "Radio", Point = [5, 5, 2], Floors = ["ground"] }],
        Links = [new() { Id = "link", Name = "Sensing link", WispId = "wisp", SourceId = "source" }]
    };

    [Test] public void EmptyStartupCreatesIsolatedSceneWithNoInventedInventory()
    {
        using var loader = Loader();
        Assert.Multiple(() =>
        {
            Assert.That(loader.Current.Config.Wisps, Is.Empty);
            Assert.That(loader.Current.Config.Sources, Is.Empty);
            Assert.That(loader.Error, Is.Null);
            Assert.That(File.Exists(Path.Combine(_directory, "config.yaml")), Is.True);
            Assert.That(File.Exists(Path.Combine(_directory, "config.last-valid.yaml")), Is.True);
        });
    }

    [Test] public async Task MetadataAndPlacementPersistAndReturnedSnapshotsAreDefensiveCopies()
    {
        using var loader = Loader();
        var saved = await loader.UpdateAsync(Scene(), loader.Current.Revision);
        var revision = saved.Revision;
        saved.Config.Wisps[0].Point[0] = 999;
        loader.Current.Config.Name = "Mutated";
        Assert.That(loader.Current.Config.Wisps[0].Point[0], Is.EqualTo(1));
        using var reloaded = Loader();
        Assert.That(reloaded.Current.Revision, Is.EqualTo(revision));
        Assert.That(reloaded.Current.Config.Wisps[0].ExternalId, Is.EqualTo("firmware-id"));
        Assert.That(reloaded.Current.Config.Links[0].SourceId, Is.EqualTo("source"));
        Assert.That(reloaded.GetYaml(), Does.Contain("schema_version: 1"));
    }

    [Test] public async Task ViewKeepsYamlSceneAndRevisionTogetherAndDefensivelyClonesScene()
    {
        using var loader = Loader();
        await loader.UpdateAsync(Scene(), loader.Current.Revision);
        var view = loader.GetView();
        var edit = Scene(); edit.Name = "New scene";
        await loader.UpdateAsync(edit, view.Revision);
        Assert.That(view.Yaml, Does.Contain("Fictional test house"));
        Assert.That(view.Config.Name, Is.EqualTo("Fictional test house"));
        Assert.That(ConfigLoader.Snapshot(view.Config).Revision, Is.EqualTo(view.Revision));
        view.Config.Wisps[0].Point[0] = 999;
        Assert.That(loader.GetView().Config.Wisps[0].Point[0], Is.EqualTo(1));
    }

    [Test] public void SnapshotHashCoversGeometryAndIsReproducible()
    {
        var scene = Scene(); var first = ConfigLoader.Snapshot(scene);
        Assert.That(ConfigLoader.Snapshot(Scene()).Revision, Is.EqualTo(first.Revision));
        scene.Wisps[0].Point[0] = 2;
        Assert.That(ConfigLoader.Snapshot(scene).Revision, Is.Not.EqualTo(first.Revision));
        Assert.That(first.Config.Wisps[0].Point[0], Is.EqualTo(1));
    }

    [Test] public async Task StaleRevisionAndExternalInvalidEditCannotBeOverwritten()
    {
        using var loader = Loader(); var revision = loader.Current.Revision;
        await loader.UpdateAsync(Scene(), revision);
        Assert.ThrowsAsync<ConfigConflictException>(() => loader.UpdateAsync(new Config(), revision));
        await File.WriteAllTextAsync(Path.Combine(_directory, "config.yaml"), "mqtt:\n  password: sentinel-secret\n");
        Assert.ThrowsAsync<ConfigConflictException>(() => loader.UpdateAsync(new Config(), loader.Current.Revision));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(_directory, "config.yaml")), Does.Contain("sentinel-secret"));
    }

    [TestCase("")]
    [TestCase("null")]
    [TestCase("schema_version: 1\nname: null\n")]
    [TestCase("schema_version: 1\nmap: null\n")]
    [TestCase("schema_version: 1\nfloors: [{id: floor, name: Name, bounds: null}]\n")]
    [TestCase("mqtt:\n  password: sentinel-secret\n")]
    [TestCase("schema_version: 999\n")]
    [TestCase("schema_version: 1\nwisps: null\n")]
    [TestCase("schema_version: 1\nfloors: [null]\n")]
    [TestCase("schema_version: 1\nname: A\nname: B\n")]
    [TestCase("schema_version: 1\nname: &alias Name\nwisps: *alias\n")]
    public void BadYamlIsRejectedWithoutPublishingInputOrDestroyingCurrentScene(string yaml)
    {
        using var loader = Loader(); var revision = loader.Current.Revision;
        var ex = Assert.ThrowsAsync<ConfigValidationException>(() => loader.UpdateYamlAsync(yaml, revision));
        Assert.That(ex!.ToString(), Does.Not.Contain("sentinel-secret"));
        Assert.That(loader.Current.Revision, Is.EqualTo(revision));
        Assert.That(loader.GetYaml(), Does.Not.Contain("sentinel-secret"));
    }

    [Test] public void InvalidCredentialInputIsAbsentFromPublishedSceneAndLogs()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "config.yaml"), "mqtt:\n  username: sentinel-user\n  password: sentinel-secret\n");
        var logger = new RecordingLogger();
        using var loader = new ConfigLoader(_directory, logger);
        Assert.That(logger.Messages, Is.Not.Empty);
        var published = System.Text.Json.JsonSerializer.Serialize(loader.GetView());
        Assert.That(published, Does.Not.Contain("sentinel"));
        Assert.That(string.Join("\n", logger.Messages), Does.Not.Contain("sentinel"));
    }

    [Test] public async Task ExternalRefreshKeepsLastValidAndRecoversWithoutRestart()
    {
        using var loader = Loader(); await loader.StartAsync(CancellationToken.None);
        try
        {
            var before = loader.Current.Revision;
            await File.WriteAllTextAsync(Path.Combine(_directory, "config.yaml"), "schema_version: 1\nname: Externally edited\n");
            await WaitFor(() => loader.Current.Revision != before);
            var valid = loader.Current.Revision;
            await File.WriteAllTextAsync(Path.Combine(_directory, "config.yaml"), "mqtt:\n  password: sentinel-secret\n");
            await WaitFor(() => loader.Error != null);
            Assert.That(loader.Current.Revision, Is.EqualTo(valid));
            Assert.That(loader.Error, Does.Not.Contain("sentinel-secret"));
            Assert.That(loader.GetYaml(), Does.Not.Contain("sentinel-secret"));
            await File.WriteAllTextAsync(Path.Combine(_directory, "config.yaml"), "schema_version: 1\nname: Recovered\n");
            await WaitFor(() => loader.Error == null);
            Assert.That(loader.Current.Config.Name, Is.EqualTo("Recovered"));
        }
        finally { await loader.StopAsync(CancellationToken.None); }
    }

    [Test] public async Task RestoringIdenticalFileAfterTemporaryLossClearsError()
    {
        using var loader = Loader(); await loader.StartAsync(CancellationToken.None);
        try
        {
            var yaml = loader.GetYaml(); var path = Path.Combine(_directory, "config.yaml");
            File.Delete(path);
            await WaitFor(() => loader.Error != null);
            await File.WriteAllTextAsync(path, yaml);
            await WaitFor(() => loader.Error == null);
            Assert.That(loader.GetYaml(), Is.EqualTo(yaml));
        }
        finally { await loader.StopAsync(CancellationToken.None); }
    }

    [Test] public async Task InvalidColdStartUsesValidatedBackupWithoutChangingInvalidFile()
    {
        string revision;
        using (var initial = Loader()) revision = (await initial.UpdateAsync(Scene(), initial.Current.Revision)).Revision;
        var path = Path.Combine(_directory, "config.yaml");
        await File.WriteAllTextAsync(path, "password: sentinel-secret\n");
        using var loader = Loader();
        Assert.That(loader.Current.Revision, Is.EqualTo(revision));
        Assert.That(loader.Error, Is.Not.Null);
        Assert.That(await File.ReadAllTextAsync(path), Does.Contain("sentinel-secret"));
        Assert.That(loader.GetYaml(), Does.Not.Contain("sentinel-secret"));
    }

    [Test] public void InvalidColdStartWithoutBackupKeepsHealthUsableWithEmptyScene()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(Path.Combine(_directory, "config.yaml"), "password: sentinel-secret\n");
        using var loader = Loader();
        Assert.That(loader.Error, Is.Not.Null);
        Assert.That(loader.Current.Config.Floors, Is.Empty);
        Assert.That(loader.GetYaml(), Does.Not.Contain("sentinel-secret"));
    }

    [Test] public async Task ConcurrentRevisionWritesAllowOneWinner()
    {
        using var loader = Loader(); var revision = loader.Current.Revision;
        async Task<bool> Save(string name)
        {
            try { await loader.UpdateAsync(new Config { Name = name }, revision); return true; }
            catch (ConfigConflictException) { return false; }
        }
        var results = await Task.WhenAll(Save("First"), Save("Second"));
        Assert.That(results.Count(success => success), Is.EqualTo(1));
    }

    [Test] public void InvalidReferencesNullsGeometryAndUnsupportedSchemaAreRejected()
    {
        var cases = new Action<Config>[]
        {
            c => c.SchemaVersion = 2,
            c => c.Name = " ",
            c => c.Wisps[0].ExternalId = "",
            c => c.Sources[0].RadioId = " ",
            c => c.Floors[0].Rooms[0].Points = [[0, 0], [4, 0], [2, 0], [4, 4], [0, 4]],
            c => c.Floors[0].Rooms[0].Points = [[0, 0], [4, 0], [4, 4], [2, 0], [0, 4]],
            c => c.Map = null!,
            c => c.Floors[0].Rooms = null!,
            c => c.Wisps[0].Point = [double.NaN, 0, 0],
            c => c.Sources[0].Point = [0, 0],
            c => c.Sources[0].Floors = ["missing"],
            c => c.Links[0].WispId = "missing",
            c => c.Floors[0].Bounds = [[0, 0, 0], [0, 5, 3]],
            c => c.Floors[0].Elevation = 4,
            c => c.Floors[0].Rooms[0].Points = [[0, 0], [4, 4], [0, 4], [4, 0]],
            c => c.Floors[0].Rooms[0].Points = [[0, 0], [1, 1], [2, 2]],
            c => c.Floors[0].Rooms[0].Points = [[0, 0], [11, 0], [0, 5]],
            c => c.Floors[0].Rooms[0].Color = "url(https://example.com)",
            c => c.Sources[0].Channel = 0,
            c => c.Wisps[0].Id = "invalid/id"
        };
        foreach (var change in cases)
        {
            var config = Scene(); change(config);
            Assert.Throws<ConfigValidationException>(() => ConfigValidation.Validate(config));
        }
    }

    [Test] public void ExplicitPolygonClosingPointIsAccepted()
    {
        var scene = Scene(); scene.Floors[0].Rooms[0].Points = [[0, 0], [5, 0], [5, 5], [0, 5], [0, 0]];
        Assert.DoesNotThrow(() => ConfigValidation.Validate(scene));
    }

    [Test] public void OversizedYamlIsRejectedBeforeParsing()
    {
        using var loader = Loader();
        Assert.ThrowsAsync<ConfigValidationException>(() => loader.UpdateYamlAsync(new string('x', ConfigValidation.MaxBytes + 1), loader.Current.Revision));
    }

    private sealed class RecordingLogger : ILogger<ConfigLoader>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }
}
