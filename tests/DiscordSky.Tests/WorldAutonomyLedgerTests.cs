using System.Text.Json;
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
    public async Task AppealFollowsExactDeliveredChunkAndNamedSubjectAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ledger.json");
        var now = DateTimeOffset.UtcNow;
        const string decree = "<@111>, your nickname is Copper.";
        const string petition = "Appeal: <@!111> objects.";
        static async Task WriteAsync(FileBackedWorldAutonomyLedger ledger, string run, string id, string name, DateTimeOffset at, int sequence = 1)
        {
            await ledger.RecordDispatchPendingAsync(new(id, run, sequence, "set_member_nickname", id, "{}", "digest", "schema", at), default);
            var envelope = NicknameReceipt(id, "4001", "111", name);
            await ledger.CompleteToolCallAsync(id, "succeeded", JsonSerializer.Serialize(new { Text = envelope }), null, at, default);
        }
        using (var ledger = CreateLedger(path))
        {
            await ledger.StartRunAsync(new("original", 4001, "fixture", "1", null, "model", "profile", "manifest", now.AddMinutes(-4), 5001), default);
            await ledger.StartRunAsync(new("later", 4001, "fixture", "2", null, "model", "profile", "manifest", now.AddMinutes(-2), 5001), default);
            await WriteAsync(ledger, "original", "first", "Copper", now.AddMinutes(-3));
            await WriteAsync(ledger, "later", "second", "Silver", now.AddMinutes(-1));
            await ledger.RecordRunEventAsync("original", "discord_delivery", """{"channelId":"5001","messageIds":["7001","7002"],"outcome":"partial_delivery"}""", now.AddMinutes(-2), default);
            await ledger.RecordRunEventAsync("later", "discord_delivery", """{"channelId":"5001","messageIds":["7003"],"outcome":"delivered"}""", now, default);
        }
        using var reopened = CreateLedger(path);
        var reader = (IVerifiedDeedReader)reopened;
        Assert.Equal("first", (await reader.ReadAppealedDeedAsync(4001, 5001, 7002, decree, petition, now, default))?.RequestId);
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7002, decree, "appeal: <@222>", now, default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7002, decree, "appeal: <@111> and <@222>", now, default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7003, decree, petition, now, default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5002, 7002, decree, petition, now, default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4002, 5001, 7002, decree, petition, now, default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7002, decree, petition, now.AddDays(3), default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7002, "<@111> meet <@222>, Copper", petition, now, default));
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7002, "<@111> is Copperfield", petition, now, default));
        await WriteAsync(reopened, "original", "after-speech", "Copper", now, 2);
        Assert.Equal("first", (await reader.ReadAppealedDeedAsync(4001, 5001, 7002, decree, petition, now, default))?.RequestId);
        await WriteAsync(reopened, "original", "second-match", "Copper", now.AddMinutes(-2.5), 3);
        Assert.Null(await reader.ReadAppealedDeedAsync(4001, 5001, 7002, decree, petition, now, default));
    }

    internal static string NicknameReceipt(string requestId, string guild, string member, string name) => JsonSerializer.Serialize(new
    {
        outcome = "succeeded", requestId, guild = new { id = guild },
        data = new { requestId, toolName = "set_member_nickname", resourceId = member, status = "succeeded", outcome = "verified_desired_state", verified = true,
            before = new { userId = member, nickname = "prior" }, desired = new { userId = member, nickname = name }, observed = new { userId = member, nickname = name } }
    });

    [Theory]
    [InlineData("succeeded", true, "111", "Copper", true)]
    [InlineData("failed", true, "111", "Copper", false)]
    [InlineData("succeeded", false, "111", "Copper", false)]
    [InlineData("succeeded", true, "222", "Copper", false)]
    [InlineData("succeeded", true, "111", "Silver", false)]
    public void DeedRequiresAConsistentConfirmedOperation(string status, bool verified, string observedMember, string observedName, bool qualifies)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(NicknameReceipt("req", "4001", "111", "Copper"))!;
        var data = json["data"]!;
        data["verified"] = verified;
        data["observed"]!["userId"] = observedMember;
        data["observed"]!["nickname"] = observedName;
        var now = DateTimeOffset.UtcNow;
        var call = new WorldAutonomyToolCall("call", "run", 1, "set_member_nickname", "req", "{}", "digest", "schema", status, now, now, json.ToJsonString(), null);
        Assert.Equal(qualifies, VerifiedDeed.From(call, 4001, 5001) is not null);
    }

    [Fact]
    public void PublicEffectsRequireTheCorrectDiscordMentionTypeAndAProvenTransition()
    {
        static VerifiedDeed? Project(string tool, string resource, object before, object desired, object observed)
        {
            var now = DateTimeOffset.UtcNow;
            var receipt = JsonSerializer.Serialize(new { outcome = "succeeded", requestId = "req", guild = new { id = "4001" },
                data = new { requestId = "req", toolName = tool, resourceId = resource, status = "succeeded", outcome = "verified_desired_state", verified = true, before, desired, observed } });
            return VerifiedDeed.From(new("call", "run", 1, tool, "req", "{}", "digest", "schema", "succeeded", now, now, receipt, null), 4001, 5001);
        }
        var role = new { id = "777", exists = true, name = "Treasurer" };
        var created = Project("create_role", "4001", new { }, role, role);
        Assert.True(created?.IsPubliclyNamedIn("<@&777>: Treasurer", "<@&777>"));
        Assert.False(created?.IsPubliclyNamedIn("<@&777>: Treasurer", "<@777>"));
        var oldChannel = new { id = "5001", name = "general" };
        var channel = new { id = "5001", name = "treasury" };
        Assert.True(Project("update_channel", "5001", oldChannel, channel, channel)?.IsPubliclyNamedIn("<#5001> is treasury", "<#5001>"));
        Assert.Null(Project("update_channel", "5002", oldChannel, channel, channel));
        var before = new { userId = "111", roleIds = new[] { "1" } };
        var after = new { userId = "111", roleIds = new[] { "1", "777" } };
        var award = Project("set_member_role", "111", before, after, after);
        Assert.True(award?.IsPubliclyNamedIn("<@111> now holds <@&777>", "<@&777> for <@111>"));
        Assert.False(award?.IsPubliclyNamedIn("<@111> now holds <@&777>", "<@111>"));
        Assert.Null(Project("set_member_role", "111", before, before, after));
        Assert.Null(Project("set_member_role", "111", after, before, before));
    }

    [Fact]
    public async Task OptionalLocalSqliteUsesSystemLibraryWithoutBundledNativeBinary()
    {
        using var directory = new TemporaryDirectory();
        var options = new WorldAutonomyOptions { LedgerPath = Path.Combine(directory.Path, "local.db") };
        using (var ledger = new SqliteWorldAutonomyLedger(options))
            await ledger.StartRunAsync(new("local", 42, "test", null, null, "model", "profile", "manifest", DateTimeOffset.UtcNow), default);
        using var reopened = new SqliteWorldAutonomyLedger(options);
        Assert.Equal("running", (await reopened.GetRunAsync("local", default))?.Status);
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
                    var sample = ledger.TakeIoSample();
                    Assert.Equal(17, sample.SuccessfulWrites);
                    Assert.Equal(new FileInfo(path).Length, sample.Bytes);
                    Assert.True(sample.MaxWriteMs > 0);
                    Assert.True(sample.MaxWaitMs >= 0);
                    Assert.Null(ledger.TakeIoSample().MaxWriteMs);
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