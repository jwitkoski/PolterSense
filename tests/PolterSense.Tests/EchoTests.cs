using System.Text.Json;
using NUnit.Framework;
using PolterSense.Models;
using PolterSense.Sensing;
using PolterSense.Services;

namespace PolterSense.Tests;

[TestFixture]
public class EchoTests
{
    [Test]
    public async Task ReplayIsRepeatableAndUsesRecordedGeometryAfterLiveSceneChanges()
    {
        var time = new ReplayTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var medium = new CapturingMedium();
        var live = new ObservationPipeline(time, medium);
        var changed = ConfigLoader.Snapshot(new Config { Name = "Different geometry", Floors = [] });
        var replay = new EchoReplay(new NormalizedTestAdapter(), medium);
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures/synthetic-echo.json");
        var first = await replay.RunAsync(path, live, time, CancellationToken.None);
        var second = await replay.RunAsync(path, live, time, CancellationToken.None);
        Assert.That(JsonSerializer.Serialize(first), Is.EqualTo(JsonSerializer.Serialize(second)));
        Assert.That(first.Accepted, Is.EqualTo(3));
        Assert.That(first.Rejected, Is.Zero);
        Assert.That(first.Medium.Rooms.Select(r => r.RoomId), Is.EquivalentTo(new[] { "studio", "kitchen", "loft" }));
        Assert.That(first.Medium.Rooms.All(r => r.State == "unknown"), Is.True);
        Assert.That(first.Medium.Tracks, Is.Null);
        Assert.That(medium.Revisions.Distinct(), Is.EqualTo(new[] { first.ConfigRevision }));
        Assert.That(first.ConfigRevision, Is.Not.EqualTo(changed.Revision));
        var displayed = live.Snapshot(changed);
        Assert.That(displayed.Medium.ConfigRevision, Is.EqualTo(changed.Revision));
        Assert.That(displayed.Medium.Rooms, Is.Empty);
        Assert.That(displayed.Medium.InputHealth, Is.EqualTo("noInput"));
        Assert.That(displayed.Diagnostics.Latest.Single().Freshness, Is.EqualTo("historicalReplay"));
        Assert.That(displayed.Diagnostics.Latest.Single().Provenance.Origin, Is.EqualTo(ObservationOrigin.Replay));
    }

    [Test]
    public async Task ReplayCannotOverwriteCurrentInputHealthOrSummaryForSameWisp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures/synthetic-echo.json");
        var echo = JsonSerializer.Deserialize<EchoSession>(File.ReadAllText(path), ContractJson.Options)!;
        var scene = ConfigLoader.Snapshot(echo.Scene);
        var time = new ReplayTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var adapter = new NormalizedTestAdapter();
        var medium = new NoOpMedium();
        var pipeline = new ObservationPipeline(time, medium);
        using var cancellation = new CancellationTokenSource();
        var processing = pipeline.RunAsync(cancellation.Token);
        try
        {
            var observation = adapter.Decode(JsonSerializer.SerializeToUtf8Bytes(echo.Observations[0], ContractJson.Options), new(time.GetUtcNow(), false));
            await pipeline.SubmitAsync(observation, scene, cancellation.Token);
            await new EchoReplay(adapter, medium).RunAsync(path, pipeline, time, cancellation.Token);
            var displayed = pipeline.Snapshot(scene);
            Assert.That(displayed.Medium.InputHealth, Is.EqualTo("receiving"));
            Assert.That(displayed.Diagnostics.Latest, Has.Length.EqualTo(2));
            Assert.That(displayed.Diagnostics.Latest.Select(o => o.Provenance.Origin), Is.EquivalentTo(new[] { ObservationOrigin.Synthetic, ObservationOrigin.Replay }));
            Assert.That(displayed.Medium.Tracks, Is.Null);
        }
        finally { cancellation.Cancel(); await processing; }
    }

    [Test]
    public void TamperedReplayRevisionIsRejectedBeforeAdmission()
    {
        var fixture = JsonSerializer.Deserialize<EchoSession>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/synthetic-echo.json")), ContractJson.Options)!;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture with { ConfigRevision = "changed" }, ContractJson.Options));
        try
        {
            var pipeline = new ObservationPipeline(TimeProvider.System, new NoOpMedium());
            Assert.ThrowsAsync<ObservationException>(async () => await new EchoReplay(new NormalizedTestAdapter(), new NoOpMedium())
                .RunAsync(path, pipeline, TimeProvider.System, CancellationToken.None));
            Assert.That(pipeline.Snapshot(ConfigLoader.Snapshot(new Config())).Diagnostics.Accepted, Is.Zero);
        }
        finally { File.Delete(path); }
    }

    private sealed class CapturingMedium : IMedium
    {
        public List<string> Revisions { get; } = [];
        public ValueTask<MediumResult> EvaluateAsync(NormalizedObservation? observation, ConfigSnapshot scene, DateTimeOffset now, CancellationToken ct)
        {
            Revisions.Add(scene.Revision);
            return new NoOpMedium().EvaluateAsync(observation, scene, now, ct);
        }
    }
}
