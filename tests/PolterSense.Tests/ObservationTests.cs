using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using PolterSense.Models;
using PolterSense.Sensing;
using PolterSense.Services;

namespace PolterSense.Tests;

[TestFixture]
public class ObservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
    private ConfigSnapshot scene = null!;
    private ReplayTimeProvider clock = null!;
    private ObservationPipeline pipeline = null!;
    private CancellationTokenSource stopping = null!;
    private Task running = null!;
    private readonly NormalizedTestAdapter adapter = new();

    [SetUp]
    public void SetUp()
    {
        scene = ConfigLoader.Snapshot(new Config
        {
            Floors = [new() { Id = "ground", Name = "Fictional ground", Bounds = [[0, 0, 0], [8, 8, 3]],
                Rooms = [new() { Id = "room", Name = "Fictional room", Points = [[0, 0], [4, 0], [4, 4], [0, 4]] }] }],
            Wisps = [new() { Id = "wisp", Name = "Display name", ExternalId = "external-receiver", Point = [1, 1, 1], Floors = ["ground"] }],
            Sources = [new() { Id = "source", Name = "Configured radio", Bssid = "02:00:00:00:00:01", Channel = 6, Point = [6, 6, 2] }],
            Links = [new() { Id = "link", Name = "Configured relationship", WispId = "wisp", SourceId = "source" }]
        });
        clock = new(Now);
        pipeline = new(clock, new NoOpMedium());
        stopping = new();
        running = pipeline.RunAsync(stopping.Token);
    }

    [TearDown]
    public async Task TearDown()
    {
        stopping.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Dispose();
    }

    private TestObservation Input() => new(1, "motion", 1, "wisp", null, null, scene.Revision,
        null, null, new(null, null, ClockBasis.Unknown), null, null, new(true, 0.8), new(true));
    private NormalizedObservation Observation() => adapter.Decode(JsonSerializer.SerializeToUtf8Bytes(Input(), ContractJson.Options), new(Now, false));
    private Task<Admission> Submit(NormalizedObservation observation) => pipeline.SubmitAsync(observation, scene, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    private void AssertAdapterFailure(byte[] payload, string code, bool retained = false)
    {
        var error = Assert.Throws<ObservationException>(() => adapter.Decode(payload, new(Now, retained)));
        Assert.That(error!.Code, Is.EqualTo(code));
    }

    [Test]
    public void AdapterPreservesMissingMetadataWithoutInventingRadioOrAcquisitionTime()
    {
        var input = Input() with { SourceId = "source", LinkId = "link" };
        var output = adapter.Decode(JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options), new(Now, false));
        Assert.Multiple(() =>
        {
            Assert.That(output.WispId, Is.EqualTo("wisp"));
            Assert.That(output.SourceId, Is.EqualTo("source"));
            Assert.That(output.ObservedRadio, Is.Null);
            Assert.That(output.Firmware, Is.Null);
            Assert.That(output.Timing.AcquiredAt, Is.Null);
            Assert.That(output.ReceivedAt, Is.EqualTo(Now));
            Assert.That(output.Provenance.Origin, Is.EqualTo(ObservationOrigin.Synthetic));
            Assert.That(output.Provenance.ReferenceOrigin, Is.EqualTo("configured"));
            Assert.That(output.Provenance.RadioOrigin, Is.EqualTo("unknown"));
            Assert.That(adapter.Capabilities.RawCsi, Is.False);
            Assert.That(adapter.Capabilities.FirmwareControl, Is.False);
        });
    }

    [Test]
    public void AdapterPreservesObservedRadioIndependentlyOfConfiguredRadio()
    {
        var input = Input() with { SourceId = "source", LinkId = "link", ObservedRadio = new("reported-radio", "02:00:00:00:00:02", 11) };
        var output = adapter.Decode(JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options), new(Now, false, true));
        Assert.That(output.ObservedRadio, Is.EqualTo(input.ObservedRadio));
        Assert.That(output.Provenance.Origin, Is.EqualTo(ObservationOrigin.Replay));
    }

    [TestCase("{")]
    [TestCase("null")]
    [TestCase("[]")]
    public void MalformedJsonIsRejected(string json) => AssertAdapterFailure(Encoding.UTF8.GetBytes(json), "malformed");

    [TestCase("surprise", "true")]
    [TestCase("motion", "null")]
    [TestCase("quality", "null")]
    [TestCase("timing", "null")]
    public void UnknownFieldsAndNullRequiredPayloadsAreRejected(string name, string value)
    {
        var json = JsonSerializer.SerializeToNode(Input(), ContractJson.Options)!;
        json[name] = JsonNode.Parse(value);
        AssertAdapterFailure(Encoding.UTF8.GetBytes(json.ToJsonString()), "malformed");
    }

    [Test]
    public void DuplicatePropertiesIncludingCaseVariantsAndNestedFieldsAreRejected()
    {
        var json = JsonSerializer.Serialize(Input(), ContractJson.Options);
        AssertAdapterFailure(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"SchemaVersion\":1")), "malformed");
        AssertAdapterFailure(Encoding.UTF8.GetBytes(json.Replace("\"score\":0.8", "\"score\":0.8,\"score\":0.7")), "malformed");
    }

    [Test]
    public void OversizedAndRetainedMessagesAreRejectedBeforeDecode()
    {
        AssertAdapterFailure(new byte[NormalizedTestAdapter.MaxPayloadBytes + 1], "oversized");
        AssertAdapterFailure(JsonSerializer.SerializeToUtf8Bytes(Input(), ContractJson.Options), "retained", true);
        pipeline.Reject("retained");
        Assert.That(pipeline.Snapshot(scene).Diagnostics.Retained, Is.EqualTo(1));
        Assert.That(pipeline.Snapshot(scene).Diagnostics.Accepted, Is.Zero);
    }

    [TestCase("schemaVersion", "2")]
    [TestCase("kindVersion", "2")]
    [TestCase("kind", "\"rawCsi\"")]
    public void UnsupportedContractVersionsAndKindsAreRejected(string name, string value)
    {
        var json = JsonSerializer.SerializeToNode(Input(), ContractJson.Options)!;
        json[name] = JsonNode.Parse(value);
        AssertAdapterFailure(Encoding.UTF8.GetBytes(json.ToJsonString()), "unsupported");
    }

    [Test]
    public async Task InvalidReferencesAndRevisionsNeverReachInference()
    {
        var original = Observation();
        foreach (var (observation, code) in new[]
        {
            (original with { ConfigRevision = "different" }, "revisionMismatch"),
            (original with { WispId = "unknown" }, "unknownWisp"),
            (original with { SourceId = "unknown" }, "unknownSource"),
            (original with { LinkId = "link" }, "invalidLink"),
            (original with { SourceId = "source", LinkId = "unknown" }, "invalidLink")
        }) Assert.That(await Submit(observation), Is.EqualTo(new Admission(false, code)));
        var snapshot = pipeline.Snapshot(scene);
        Assert.That(snapshot.Diagnostics.Rejected, Is.EqualTo(5));
        Assert.That(snapshot.Diagnostics.Accepted, Is.Zero);
        Assert.That(snapshot.Medium.Rooms.Single().State, Is.EqualTo("unknown"));
        Assert.That(await Submit(original with { SourceId = "source", LinkId = "link" }), Is.EqualTo(new Admission(true, "accepted")));
    }

    [Test]
    public async Task InvalidFeatureQualityAndNullHeadlessPayloadsAreRejected()
    {
        var original = Observation();
        foreach (var (observation, code) in new[]
        {
            (original with { Motion = null! }, "malformed"),
            (original with { Quality = null! }, "malformed"),
            (original with { Timing = null! }, "malformed"),
            (original with { Provenance = null! }, "malformed"),
            (original with { Motion = new(null, null) }, "emptyFeatures"),
            (original with { Motion = new(true, double.NaN) }, "invalidFeatures"),
            (original with { Motion = new(true, 1.1) }, "invalidFeatures"),
            (original with { Quality = new(true, -0.1) }, "invalidQuality"),
            (original with { Quality = new(true, null, -1) }, "invalidQuality")
        }) Assert.That(await Submit(observation), Is.EqualTo(new Admission(false, code)));
        Assert.That(pipeline.Snapshot(scene).Diagnostics.Rejected, Is.EqualTo(9));
    }

    [Test]
    public async Task TimingMustStateWhatWasActuallyMeasured()
    {
        var original = Observation();
        foreach (var timing in new ObservationTiming[]
        {
            new(Now, null, ClockBasis.Unknown), new(null, null, ClockBasis.ReportedUtc),
            new(null, null, ClockBasis.DeviceMonotonic), new(Now, 123, ClockBasis.DeviceMonotonic)
        }) Assert.That(await Submit(original with { Timing = timing }), Is.EqualTo(new Admission(false, "invalidTiming")));
        Assert.That((await Submit(original with { Timing = new(null, 100, ClockBasis.DeviceMonotonic) })).Accepted, Is.True);
        var summary = pipeline.Snapshot(scene).Diagnostics.Latest.Single();
        Assert.That(summary.AcquiredAt, Is.Null);
        Assert.That(summary.ClockBasis, Is.EqualTo(ClockBasis.DeviceMonotonic));
        Assert.That(summary.Freshness, Is.EqualTo("arrivalOnly"));
    }

    [Test]
    public async Task StaleAndFutureUtcEvidenceIsRejectedButHistoricalReplayIsLabeled()
    {
        var original = Observation();
        Assert.That(await Submit(original with { Timing = new(Now.AddSeconds(-31), null, ClockBasis.ReportedUtc) }), Is.EqualTo(new Admission(false, "stale")));
        Assert.That(await Submit(original with { Timing = new(Now.AddSeconds(6), null, ClockBasis.ReportedUtc) }), Is.EqualTo(new Admission(false, "future")));
        Assert.That(await Submit(original with { ReceivedAt = Now.AddSeconds(-31) }), Is.EqualTo(new Admission(false, "stale")));
        var replay = original with { Timing = new(Now.AddYears(-1), null, ClockBasis.ReportedUtc), Provenance = original.Provenance with { Origin = ObservationOrigin.Replay } };
        Assert.That((await Submit(replay)).Accepted, Is.True);
        var snapshot = pipeline.Snapshot(scene);
        Assert.That(snapshot.Diagnostics.Stale, Is.EqualTo(2));
        Assert.That(snapshot.Diagnostics.Latest.Single().Freshness, Is.EqualTo("historicalReplay"));
        Assert.That(snapshot.Medium.InputHealth, Is.EqualTo("noInput"));
    }

    [Test]
    public async Task OrderingUsesSessionAndSequenceAndDoesNotAdvanceAfterRejection()
    {
        var original = Observation() with { SessionId = "session", Sequence = 10, Timing = new(null, 100, ClockBasis.DeviceMonotonic) };
        Assert.That((await Submit(original)).Accepted, Is.True);
        Assert.That(await Submit(original), Is.EqualTo(new Admission(false, "duplicate")));
        Assert.That(await Submit(original with { Sequence = 9 }), Is.EqualTo(new Admission(false, "outOfOrder")));
        Assert.That(await Submit(original with { Sequence = 11, Timing = new(null, 99, ClockBasis.DeviceMonotonic) }), Is.EqualTo(new Admission(false, "outOfOrder")));
        Assert.That((await Submit(original with { Sequence = 11, Timing = new(null, 101, ClockBasis.DeviceMonotonic) })).Accepted, Is.True);
        Assert.That((await Submit(original with { SessionId = "new-session", Sequence = 0 })).Accepted, Is.True);
        var diagnostics = pipeline.Snapshot(scene).Diagnostics;
        Assert.That(diagnostics.Accepted, Is.EqualTo(3));
        Assert.That(diagnostics.Duplicate, Is.EqualTo(1));
        Assert.That(diagnostics.OutOfOrder, Is.EqualTo(2));
        Assert.That(diagnostics.Rejected, Is.EqualTo(3));
    }

    [TestCase(null, null)]
    [TestCase("session", null)]
    [TestCase(null, 10L)]
    public async Task IncompleteIdentityDoesNotInventOrdering(string? session, long? sequence)
    {
        var original = Observation() with { SessionId = session, Sequence = sequence };
        Assert.That((await Submit(original)).Accepted, Is.True);
        Assert.That((await Submit(original)).Accepted, Is.True);
        Assert.That(pipeline.Snapshot(scene).Diagnostics.Duplicate, Is.Zero);
    }

    [Test]
    public async Task NoMotionAndStaleInputNeverBecomeVacancyOrEmptyTracks()
    {
        Assert.That(pipeline.Snapshot(scene).Medium.InputHealth, Is.EqualTo("noInput"));
        await Submit(Observation() with { Motion = new(false, 0) });
        Assert.That(pipeline.Snapshot(scene).Medium.InputHealth, Is.EqualTo("receiving"));
        clock.Advance(TimeSpan.FromSeconds(31));
        var snapshot = pipeline.Snapshot(scene);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Medium.Availability, Is.EqualTo("notImplemented"));
            Assert.That(snapshot.Medium.InputHealth, Is.EqualTo("stale"));
            Assert.That(snapshot.Medium.Tracks, Is.Null);
            Assert.That(snapshot.Medium.Rooms.Single().State, Is.EqualTo("unknown"));
            Assert.That(snapshot.Medium.Rooms.Single().EstimatedAt, Is.Null);
            Assert.That(snapshot.Medium.Rooms.Single().Confidence, Is.Null);
            Assert.That(snapshot.Diagnostics.Latest.Single().Freshness, Is.EqualTo("stale"));
        });
    }

    [Test]
    public async Task MissingReadinessRemainsNotReady()
    {
        await Submit(Observation() with { Quality = new(null) });
        Assert.That(pipeline.Snapshot(scene).Medium.InputHealth, Is.EqualTo("notReady"));
    }

    [Test]
    public async Task AcquiredEvidenceExpiresEvenWhenRecentlyReceived()
    {
        await Submit(Observation() with { Timing = new(Now.AddSeconds(-29), null, ClockBasis.ReportedUtc) });
        clock.Advance(TimeSpan.FromSeconds(2));
        var state = pipeline.Snapshot(scene);
        Assert.That(state.Diagnostics.Latest.Single().Freshness, Is.EqualTo("stale"));
        Assert.That(state.Medium.InputHealth, Is.EqualTo("stale"));
        Assert.That(state.Medium.Rooms.All(r => r.State == "unknown"), Is.True);
    }

    [Test]
    public async Task ChangedSceneDoesNotInterpretEarlierObservationsAsCurrentInput()
    {
        await Submit(Observation());
        var changed = scene.Config;
        changed.Name = "Changed geometry context";
        var next = ConfigLoader.Snapshot(changed);
        var snapshot = pipeline.Snapshot(next);
        Assert.That(snapshot.Medium.ConfigRevision, Is.EqualTo(next.Revision));
        Assert.That(snapshot.Medium.InputHealth, Is.EqualTo("noInput"));
        Assert.That(snapshot.Diagnostics.Latest.Single().ConfigRevision, Is.EqualTo(scene.Revision));
    }

    [Test]
    public async Task QueueCapacityRejectsOverflowAndDrainsAcceptedWork()
    {
        var bounded = new ObservationPipeline(clock, new NoOpMedium());
        var pending = Enumerable.Range(0, ObservationPipeline.Capacity)
            .Select(_ => bounded.SubmitAsync(Observation(), scene, CancellationToken.None).AsTask()).ToArray();
        var overflow = await bounded.SubmitAsync(Observation(), scene, CancellationToken.None);
        Assert.That(overflow, Is.EqualTo(new Admission(false, "queueFull")));
        Assert.That(bounded.Snapshot(scene).Diagnostics.Dropped, Is.EqualTo(1));
        using var stop = new CancellationTokenSource();
        var runner = bounded.RunAsync(stop.Token);
        try
        {
            var admissions = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(admissions.All(a => a.Accepted), Is.True);
            Assert.That(bounded.Snapshot(scene).Diagnostics.Accepted, Is.EqualTo(ObservationPipeline.Capacity));
        }
        finally { stop.Cancel(); await runner.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Test]
    public async Task CancelledQueuedWorkNeverReachesMedium()
    {
        var bounded = new ObservationPipeline(clock, new NoOpMedium());
        using var request = new CancellationTokenSource();
        var pending = bounded.SubmitAsync(Observation(), scene, request.Token).AsTask();
        request.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await pending);
        using var stop = new CancellationTokenSource();
        var runner = bounded.RunAsync(stop.Token);
        // A subsequent accepted item proves the cancelled queue entry was traversed.
        await bounded.SubmitAsync(Observation(), scene, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await runner.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(bounded.Snapshot(scene).Diagnostics.Accepted, Is.EqualTo(1));
    }

    [Test]
    public void NoOpMediumHonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new NoOpMedium().EvaluateAsync(Observation(), scene, Now, cancelled.Token));
    }
}
