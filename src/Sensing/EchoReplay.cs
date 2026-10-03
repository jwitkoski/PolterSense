using System.Text.Json;
using PolterSense.Models;
using PolterSense.Services;

namespace PolterSense.Sensing;

public record EchoSession(int SchemaVersion, string Id, string Provenance, string Fixture, string ConfigRevision, Config Scene, TestObservation[] Observations);
public record EchoResult(string Id, string Provenance, string ConfigRevision, int Accepted, int Rejected, MediumResult Medium);
public sealed class EchoReplay(IObservationAdapter adapter, IMedium medium)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<EchoResult> RunAsync(string path, ObservationPipeline pipeline, TimeProvider time, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new ObservationException("replayBusy");
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 512 * 1024) throw new ObservationException("oversizedEcho");
            var echo = JsonSerializer.Deserialize<EchoSession>(await File.ReadAllTextAsync(path, ct), ContractJson.Options)
                ?? throw new ObservationException("malformedEcho");
            if (echo.SchemaVersion != 1 || echo.Provenance != "synthetic" || echo.Observations is null || echo.Observations.Length > 256)
                throw new ObservationException("unsupportedEcho");
            var scene = ConfigLoader.Snapshot(echo.Scene);
            if (scene.Revision != echo.ConfigRevision || echo.Observations.Any(o => o.ConfigRevision != scene.Revision))
                throw new ObservationException("revisionMismatch");
            // A replay-local pipeline makes repeated runs deterministic without weakening live deduplication.
            var replayTime = new ReplayTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            var replayPipeline = new ObservationPipeline(replayTime, medium);
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var processing = replayPipeline.RunAsync(runCancellation.Token);
            int accepted = 0, rejected = 0;
            try
            {
                foreach (var input in echo.Observations)
                {
                    ct.ThrowIfCancellationRequested();
                    var payload = JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options);
                    var normalized = adapter.Decode(payload, new(replayTime.GetUtcNow(), false, true));
                    var admission = await replayPipeline.SubmitAsync(normalized, scene, ct);
                    if (admission.Accepted)
                    {
                        accepted++;
                        // Same adapter/admission/Medium boundary, plus explicitly historical diagnostics in the UI.
                        pipeline.RecordReplayDiagnostic(normalized with { ReceivedAt = time.GetUtcNow() });
                    }
                    else rejected++;
                    replayTime.Advance(TimeSpan.FromMilliseconds(100));
                }
                return new(echo.Id, echo.Provenance, scene.Revision, accepted, rejected, replayPipeline.Snapshot(scene).Medium);
            }
            finally { runCancellation.Cancel(); await processing; }
        }
        finally { gate.Release(); }
    }
}
public sealed class ReplayTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset now = initial;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
