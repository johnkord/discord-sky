using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyLedgerTests
{
    [Fact]
    public async Task PendingDispatch_PersistsCanonicalEvidenceAcrossLedgerInstances()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "world-autonomy.json");
        var run = new WorldAutonomyRunStart(
            "run-1",
            667956000757776386,
            "message",
            "100",
            "episode-1",
            "gpt-5.5",
            "profile-digest",
            "manifest-digest",
            DateTimeOffset.Parse("2026-07-28T12:00:00Z"));
        var arguments = WorldAutonomyCanonicalizer.SerializeArguments(new Dictionary<string, object?>
        {
            ["reason"] = "make a channel stranger",
            ["request_id"] = "01900000-0000-7000-8000-000000000001",
            ["input"] = new Dictionary<string, object?> { ["name"] = "laboratory", ["position"] = 2 }
        });
        var dispatch = new WorldAutonomyPendingDispatch(
            "call-1",
            run.RunId,
            1,
            "create_text_channel",
            "01900000-0000-7000-8000-000000000001",
            arguments,
            WorldAutonomyCanonicalizer.ComputeDigest(arguments),
            "schema-digest",
            run.StartedAt.AddSeconds(1));

        using (var ledger = CreateLedger(path))
        {
            await ledger.StartRunAsync(run, CancellationToken.None);
            await ledger.RecordDispatchPendingAsync(dispatch, CancellationToken.None);
        }

        using var reopened = CreateLedger(path);
        var recoverable = await reopened.ListRecoverableCallsAsync(CancellationToken.None);
        var storedRun = await reopened.GetRunAsync(run.RunId, CancellationToken.None);

        var stored = Assert.Single(recoverable);
        Assert.Equal(dispatch.CallId, stored.CallId);
        Assert.Equal(dispatch.RunId, stored.RunId);
        Assert.Equal(dispatch.Sequence, stored.Sequence);
        Assert.Equal(dispatch.ToolName, stored.ToolName);
        Assert.Equal(dispatch.RequestId, stored.RequestId);
        Assert.Equal(dispatch.ArgumentsDigest, stored.ArgumentsDigest);
        Assert.Equal(dispatch.SchemaDigest, stored.SchemaDigest);
        Assert.Equal(WorldAutonomyDispatchStatuses.Pending, stored.DispatchStatus);
        Assert.Equal(dispatch.CreatedAt, stored.CreatedAt);
        Assert.Equal("{\"input\":{\"name\":\"laboratory\",\"position\":2},\"reason\":\"make a channel stranger\",\"request_id\":\"01900000-0000-7000-8000-000000000001\"}", stored.ArgumentsJson);
        Assert.Equal(WorldAutonomyRunStatuses.Running, storedRun?.Status);
    }

    [Fact]
    public async Task CompletedDispatch_IsNotReturnedForRecovery()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = CreateLedger(Path.Combine(directory.Path, "world-autonomy.json"));
        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        await ledger.StartRunAsync(new WorldAutonomyRunStart(
            "run-1", 667956000757776386, "message", null, null, "gpt-5.5", "profile", "manifest", now), CancellationToken.None);
        await ledger.RecordDispatchPendingAsync(new WorldAutonomyPendingDispatch(
            "call-1", "run-1", 1, "update_channel", "01900000-0000-7000-8000-000000000001",
            "{}", WorldAutonomyCanonicalizer.ComputeDigest("{}"), "schema", now), CancellationToken.None);

        await ledger.CompleteToolCallAsync(
            "call-1",
            WorldAutonomyDispatchStatuses.Succeeded,
            "{\"status\":\"succeeded\"}",
            errorMessage: null,
            now.AddSeconds(2),
            CancellationToken.None);
        await ledger.CompleteRunAsync(
            "run-1",
            WorldAutonomyRunStatuses.Succeeded,
            "done",
            failureReason: null,
            now.AddSeconds(3),
            CancellationToken.None);

        Assert.Empty(await ledger.ListRecoverableCallsAsync(CancellationToken.None));
        Assert.Equal(WorldAutonomyRunStatuses.Succeeded, (await ledger.GetRunAsync("run-1", CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task ConcurrentDispatches_PersistEveryRecordWithoutTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "world-autonomy.json");
        var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
        using (var ledger = CreateLedger(path))
        {
            await ledger.StartRunAsync(new WorldAutonomyRunStart(
                "run-1", 667956000757776386, "message", null, null, "gpt-5.5", "profile", "manifest", now), CancellationToken.None);

            await Task.WhenAll(Enumerable.Range(1, 16).Select(sequence => ledger.RecordDispatchPendingAsync(
                new WorldAutonomyPendingDispatch(
                    $"call-{sequence}",
                    "run-1",
                    sequence,
                    "update_channel",
                    $"01900000-0000-7000-8000-{sequence:D12}",
                    "{}",
                    WorldAutonomyCanonicalizer.ComputeDigest("{}"),
                    "schema",
                    now.AddSeconds(sequence)),
                CancellationToken.None)));
        }

        using var reopened = CreateLedger(path);
        var recoverable = await reopened.ListRecoverableCallsAsync(CancellationToken.None);

        Assert.Equal(16, recoverable.Count);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private static FileBackedWorldAutonomyLedger CreateLedger(string path) => new(new WorldAutonomyOptions
    {
        LedgerPath = path
    });

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"discord-sky-autonomy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}