using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyRecoveryServiceTests
{
    private static readonly DateTimeOffset ProcessStartedAt =
        DateTimeOffset.Parse("2026-08-01T12:00:00Z");

    [Fact]
    public async Task RecoveryCycle_TerminalizesAPriorProcessRunThatNeverDispatched()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "world-autonomy.json");
        using var ledger = CreateLedger(path);
        await ledger.StartRunAsync(
            Run("run-before-dispatch", ProcessStartedAt.AddMinutes(-2)),
            CancellationToken.None);
        await using var supervisor = CreateSupervisor();
        using var service = CreateService(ledger, supervisor);

        await service.RunRecoveryCycleAsync(ProcessStartedAt, CancellationToken.None);

        var recovered = await ledger.GetRunAsync("run-before-dispatch", CancellationToken.None);
        Assert.Equal(WorldAutonomyRunStatuses.Failed, recovered?.Status);
        Assert.Equal("process_restarted_before_completion", recovered?.FailureReason);
        Assert.Equal(ProcessStartedAt, recovered?.CompletedAt);
        Assert.Contains("\"kind\": \"run_interrupted\"", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RecoveryCycle_TerminalizesAPriorProcessRunAfterItsCallsSettled()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "world-autonomy.json");
        using var ledger = CreateLedger(path);
        await ledger.StartRunAsync(
            Run("run-after-write", ProcessStartedAt.AddMinutes(-2)),
            CancellationToken.None);
        await ledger.RecordDispatchPendingAsync(
            Dispatch("call-1", "run-after-write", ProcessStartedAt.AddMinutes(-1)),
            CancellationToken.None);
        await ledger.CompleteToolCallAsync(
            "call-1",
            WorldAutonomyDispatchStatuses.Succeeded,
            "{\"outcome\":\"succeeded\"}",
            errorMessage: null,
            ProcessStartedAt.AddSeconds(-30),
            CancellationToken.None);
        await using var supervisor = CreateSupervisor();
        using var service = CreateService(ledger, supervisor);

        await service.RunRecoveryCycleAsync(ProcessStartedAt, CancellationToken.None);

        var recovered = await ledger.GetRunAsync("run-after-write", CancellationToken.None);
        Assert.Equal(WorldAutonomyRunStatuses.Failed, recovered?.Status);
        Assert.Equal("process_restarted_after_call_recovery", recovered?.FailureReason);
        using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var eventRecord = Assert.Single(snapshot.RootElement.GetProperty("events").EnumerateArray());
        using var payload = JsonDocument.Parse(eventRecord.GetProperty("payloadJson").GetString()!);
        Assert.Equal(1, payload.RootElement.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("totalCalls").GetInt32());
    }

    [Fact]
    public async Task RecoveryCycle_DoesNotTouchARunStartedByThisProcess()
    {
        using var directory = new TemporaryDirectory();
        using var ledger = CreateLedger(Path.Combine(directory.Path, "world-autonomy.json"));
        await ledger.StartRunAsync(
            Run("current-run", ProcessStartedAt),
            CancellationToken.None);
        await using var supervisor = CreateSupervisor();
        using var service = CreateService(ledger, supervisor);

        await service.RunRecoveryCycleAsync(ProcessStartedAt, CancellationToken.None);

        var current = await ledger.GetRunAsync("current-run", CancellationToken.None);
        Assert.Equal(WorldAutonomyRunStatuses.Running, current?.Status);
        Assert.Null(current?.CompletedAt);
    }

    private static WorldAutonomyRecoveryService CreateService(
        IWorldAutonomyLedger ledger,
        StewardMcpSupervisor supervisor) => new(
            Configuration,
            ledger,
            supervisor,
            NullLogger<WorldAutonomyRecoveryService>.Instance,
            new FixedTimeProvider(ProcessStartedAt),
            TimeSpan.FromHours(1));

    private static StewardMcpSupervisor CreateSupervisor() => new(
        Configuration,
        NullLoggerFactory.Instance,
        NullLogger<StewardMcpSupervisor>.Instance);

    private static FileBackedWorldAutonomyLedger CreateLedger(string path) => new(new WorldAutonomyOptions
    {
        LedgerPath = path
    });

    private static WorldAutonomyRunStart Run(string runId, DateTimeOffset startedAt) => new(
        runId,
        667956000757776386,
        "discord_message",
        "100",
        null,
        "gpt-5.6-sol",
        "profile",
        "manifest",
        startedAt);

    private static WorldAutonomyPendingDispatch Dispatch(
        string callId,
        string runId,
        DateTimeOffset createdAt) => new(
        callId,
        runId,
        1,
        "update_channel",
        "01900000-0000-7000-8000-000000000001",
        "{}",
        WorldAutonomyCanonicalizer.ComputeDigest("{}"),
        "schema",
        createdAt);

    private static readonly WorldAutonomyConfiguration Configuration =
        WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "unused",
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["667956000757776386"] = new() { ProfilePath = "/unused/profile.json" }
            }
        });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"discord-sky-recovery-{Guid.NewGuid():N}");
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