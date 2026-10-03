using System.Threading.Channels;
using PolterSense.Models;
using PolterSense.Services;

namespace PolterSense.Sensing;

public record Admission(bool Accepted, string Code);
public record ObservationSummary(string WispId, string? SourceId, string? LinkId, DateTimeOffset ReceivedAt,
    DateTimeOffset? AcquiredAt, double? Score, bool? Motion, bool? Ready, ObservationProvenance Provenance,
    string ConfigRevision, string Freshness, ClockBasis ClockBasis);
public record InputDiagnostics(long Accepted, long Rejected, long Dropped, long Stale, long Duplicate,
    long OutOfOrder, long Retained, string? LastRejection, DateTimeOffset? LastReceivedAt, ObservationSummary[] Latest);

// A single bounded admission queue serializes Medium calls; callers get explicit backpressure results.
// No browser fanout occurs on the sample path. Scene is captured at admission, never looked up later.
public sealed class ObservationPipeline(TimeProvider time, IMedium medium)
{
    public const int Capacity = 128;
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    private sealed record Pending(NormalizedObservation Observation, ConfigSnapshot Scene, TaskCompletionSource<Admission> Completion, CancellationToken Cancellation);
    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(Capacity)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly object sync = new();
    // Ordering is only meaningful with an explicit session AND sequence; cache has a hard cap.
    private readonly Dictionary<(string Wisp, string Session, string Revision, ObservationOrigin Origin), (long Sequence, long? DeviceTime)> order = new();
    private readonly Dictionary<(string Wisp, ObservationOrigin Origin, string Revision), NormalizedObservation> latest = new();
    private long accepted, rejected, dropped, stale, duplicate, outOfOrder, retained;
    private string? lastRejection;
    private MediumResult? result;

    public Admission Reject(string code)
    {
        lock (sync)
        {
            rejected++; lastRejection = code;
            if (code is "queueFull" or "orderingCapacity" or "stopped") dropped++;
            if (code == "stale") stale++;
            if (code == "duplicate") duplicate++;
            if (code == "outOfOrder") outOfOrder++;
            if (code == "retained") retained++;
        }
        return new(false, code);
    }
    public async ValueTask<Admission> SubmitAsync(NormalizedObservation observation, ConfigSnapshot scene, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var captured = ConfigLoader.Snapshot(scene.Config);
        if (captured.Revision != scene.Revision) return Reject("revisionMismatch");
        var completion = new TaskCompletionSource<Admission>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new(observation, captured, completion, ct))) return Reject("queueFull");
        return await completion.Task.WaitAsync(ct);
    }
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(ct))
            {
                if (item.Cancellation.IsCancellationRequested) { item.Completion.TrySetCanceled(item.Cancellation); continue; }
                try
                {
                    var issue = Validate(item.Observation, item.Scene);
                    if (issue != null) { item.Completion.TrySetResult(Reject(issue)); continue; }
                    var output = await medium.EvaluateAsync(item.Observation, item.Scene, time.GetUtcNow(), ct);
                    lock (sync)
                    {
                        var o = item.Observation;
                        if (o.SessionId is not null && o.Sequence is not null)
                            order[(o.WispId, o.SessionId, o.ConfigRevision, o.Provenance.Origin)] = (o.Sequence.Value, o.Timing.DeviceTimeMs);
                        // Bound the diagnostics buffer even over many accepted scene revisions.
                        if (latest.Count >= 128 && !latest.ContainsKey(SummaryKey(o))) latest.Remove(latest.MinBy(x => x.Value.ReceivedAt).Key);
                        latest[SummaryKey(o)] = o;
                        accepted++;
                        result = output;
                    }
                    item.Completion.TrySetResult(new(true, "accepted"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { item.Completion.TrySetResult(Reject("stopped")); }
                catch (Exception) { item.Completion.TrySetResult(Reject("processingFailed")); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var pending)) pending.Completion.TrySetResult(Reject("stopped"));
        }
    }
    private static (string, ObservationOrigin, string) SummaryKey(NormalizedObservation observation) =>
        (observation.WispId, observation.Provenance.Origin, observation.ConfigRevision);

    internal void RecordReplayDiagnostic(NormalizedObservation observation)
    {
        lock (sync)
        {
            if (latest.Count >= 128 && !latest.ContainsKey(SummaryKey(observation))) latest.Remove(latest.MinBy(x => x.Value.ReceivedAt).Key);
            latest[SummaryKey(observation)] = observation;
            accepted++;
        }
    }
    private string? Validate(NormalizedObservation o, ConfigSnapshot scene)
    {
        if (o.SchemaVersion != 1 || o.Kind != "motion" || o.KindVersion != 1) return "unsupported";
        if (o.ConfigRevision != scene.Revision) return "revisionMismatch";
        if (o.WispId is null || !scene.Config.Wisps.Any(w => w.Id == o.WispId)) return "unknownWisp";
        if (o.SourceId is not null && !scene.Config.Sources.Any(s => s.Id == o.SourceId)) return "unknownSource";
        if (o.LinkId is not null && !scene.Config.Links.Any(l => l.Id == o.LinkId && l.WispId == o.WispId && l.SourceId == o.SourceId)) return "invalidLink";
        if (o.Motion is null || o.Quality is null || o.Timing is null || o.Provenance is null) return "malformed";
        if (o.Motion.Motion is null && o.Motion.Score is null) return "emptyFeatures";
        if (o.Motion.Score is double score && (!double.IsFinite(score) || score is < 0 or > 1)) return "invalidFeatures";
        if (o.Quality.Confidence is double confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1) || o.Quality.DroppedSamples < 0) return "invalidQuality";
        if (o.SessionId is { Length: > 128 } or "" || o.Sequence < 0 || o.Timing.DeviceTimeMs < 0) return "invalidIdentity";
        if (o.Firmware?.Version?.Length > 128 || o.Firmware?.FeatureVersion?.Length > 128 || o.Firmware?.Detector?.Length > 128 || o.ObservedRadio?.RadioId?.Length > 128 || o.ObservedRadio?.Bssid?.Length > 32 || o.ObservedRadio?.Channel is < 1 or > 233) return "invalidMetadata";
        if (!Enum.IsDefined(o.Timing.ClockBasis) || !Enum.IsDefined(o.Provenance.Origin)) return "unsupported";
        if ((o.Timing.AcquiredAt is not null) != (o.Timing.ClockBasis == ClockBasis.ReportedUtc)) return "invalidTiming";
        if (o.Timing.ClockBasis == ClockBasis.Unknown && o.Timing.DeviceTimeMs is not null) return "invalidTiming";
        if (o.Timing.ClockBasis == ClockBasis.DeviceMonotonic && o.Timing.DeviceTimeMs is null) return "invalidTiming";
        var now = time.GetUtcNow();
        // Replay keeps recorded acquisition times; freshness of historical evidence is never called live.
        if (o.Provenance.Origin != ObservationOrigin.Replay && o.Timing.AcquiredAt is {} acquired)
        {
            if (now - acquired > Freshness) return "stale";
            if (acquired - now > TimeSpan.FromSeconds(5)) return "future";
        }
        if (now - o.ReceivedAt > Freshness || o.ReceivedAt > now.AddSeconds(5)) return "stale";
        if (o.SessionId is not null && o.Sequence is not null)
        {
            lock (sync)
            {
                if (order.TryGetValue((o.WispId, o.SessionId, o.ConfigRevision, o.Provenance.Origin), out var prior))
                {
                    if (o.Sequence == prior.Sequence) return "duplicate";
                    if (o.Sequence < prior.Sequence || (o.Timing.DeviceTimeMs is {} timestamp && prior.DeviceTime is {} previous && timestamp < previous)) return "outOfOrder";
                }
                else if (order.Count >= 1024) return "orderingCapacity";
            }
        }
        return null;
    }
    private static bool IsStale(NormalizedObservation observation, DateTimeOffset now) =>
        now - observation.ReceivedAt > Freshness ||
        (observation.Timing.AcquiredAt is {} acquired && now - acquired > Freshness);

    public (InputDiagnostics Diagnostics, MediumResult Medium) Snapshot(ConfigSnapshot current)
    {
        lock (sync)
        {
            var now = time.GetUtcNow();
            var observations = latest.Values.OrderBy(o => o.WispId).ThenBy(o => o.Provenance.Origin).ThenBy(o => o.ConfigRevision).Select(o => new ObservationSummary(o.WispId,
                o.SourceId, o.LinkId, o.ReceivedAt, o.Timing.AcquiredAt, o.Motion.Score, o.Motion.Motion,
                o.Quality.Ready, o.Provenance, o.ConfigRevision,
                o.Provenance.Origin == ObservationOrigin.Replay ? "historicalReplay" : IsStale(o, now) ? "stale" : o.Timing.AcquiredAt is null ? "arrivalOnly" : "recentReportedUtc", o.Timing.ClockBasis)).ToArray();
            var active = latest.Values.Where(o => o.ConfigRevision == current.Revision && o.Provenance.Origin != ObservationOrigin.Replay).ToArray();
            var health = active.Length == 0 ? "noInput" : active.All(o => IsStale(o, now)) ? "stale" : active.Any(o => o.Quality.Ready == true && !IsStale(o, now)) ? "receiving" : "notReady";
            // Current scene always remains independent of a replay's geometry.
            var output = result is not null && result.ConfigRevision == current.Revision
                ? result with { InputHealth = health } : NoOpMedium.Unknown(current, health);
            return (new(accepted, rejected, dropped, stale, duplicate, outOfOrder, retained, lastRejection,
                observations.Length == 0 ? null : observations.Max(o => o.ReceivedAt), observations), output);
        }
    }
}
