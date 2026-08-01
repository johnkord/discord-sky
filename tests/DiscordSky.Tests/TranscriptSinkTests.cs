using System.Text.Json;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class TranscriptSinkTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "discord-sky-transcript-test-" + Guid.NewGuid().ToString("N"));

    public TranscriptSinkTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Record_CorrelationFieldsProduceVersionOneRow()
    {
        var sink = BuildSink(enabled: true);
        await sink.StartAsync(CancellationToken.None);

        sink.Record(CreateEntry(
            EpisodeId: "episode-1",
            TriggerMessageId: 10,
            ReplyTargetMessageId: 11,
            EvidenceDigest: "evidence-1"));

        using var document = JsonDocument.Parse(await ReadOnlyLineAsync());
        var root = document.RootElement;
        Assert.Equal(FileBackedTranscriptSink.CurrentSchemaVersion,
            root.GetProperty("transcript_schema_version").GetInt32());
        Assert.Equal("episode-1", root.GetProperty("episode_id").GetString());
        Assert.Equal(10UL, root.GetProperty("trigger_message_id").GetUInt64());
        Assert.Equal(11UL, root.GetProperty("reply_target_message_id").GetUInt64());
        Assert.Equal("evidence-1", root.GetProperty("evidence_digest").GetString());
    }

    [Fact]
    public async Task Record_WithoutCorrelationOmitsOptionalFields()
    {
        var sink = BuildSink(enabled: true);
        await sink.StartAsync(CancellationToken.None);

        sink.Record(CreateEntry());

        var line = await ReadOnlyLineAsync();
        Assert.DoesNotContain("transcript_schema_version", line);
        Assert.DoesNotContain("episode_id", line);
        Assert.DoesNotContain("reply_target_message_id", line);
    }

    [Fact]
    public async Task Record_RateLimitedOutcomeMarksNoModelInvocation()
    {
        var sink = BuildSink(enabled: true);
        await sink.StartAsync(CancellationToken.None);

        sink.Record(CreateEntry() with
        {
            Outcome = "rate_limited_fallback",
            ModelInvoked = false,
        });

        using var document = JsonDocument.Parse(await ReadOnlyLineAsync());
        var root = document.RootElement;
        Assert.Equal(FileBackedTranscriptSink.CurrentSchemaVersion,
            root.GetProperty("transcript_schema_version").GetInt32());
        Assert.Equal("rate_limited_fallback", root.GetProperty("outcome").GetString());
        Assert.False(root.GetProperty("model_invoked").GetBoolean());
    }

    [Fact]
    public void TranscriptEntry_DeserializesVersionZeroAndFutureFields()
    {
        const string json = """
            {"ts":"2026-07-19T12:00:00+00:00","user_id":1,"user":"u","channel_id":2,"channel":"c","persona":"p","kind":"Ambient","prompt":"hello","reply":"hi","future_field":true}
            """;

        var entry = JsonSerializer.Deserialize<TranscriptEntry>(json);

        Assert.NotNull(entry);
        Assert.Equal("hi", entry!.Reply);
        Assert.Null(entry.TranscriptSchemaVersion);
        Assert.Null(entry.EpisodeId);
    }

    [Fact]
    public async Task Record_WhenDisabledDoesNotCreateFile()
    {
        var sink = BuildSink(enabled: false);
        await sink.StartAsync(CancellationToken.None);

        sink.Record(CreateEntry(EpisodeId: "episode-1"));

        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }

    private FileBackedTranscriptSink BuildSink(bool enabled) => new(
        Options.Create(new TranscriptOptions
        {
            Enabled = enabled,
            BaseDirectory = _tempDir,
            RetentionDays = 14,
        }),
        NullLogger<FileBackedTranscriptSink>.Instance);

    private static TranscriptEntry CreateEntry(
        string? EpisodeId = null,
        ulong? TriggerMessageId = null,
        ulong? ReplyTargetMessageId = null,
        string? EvidenceDigest = null) => new(
            Timestamp: new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero),
            UserId: 1,
            UserDisplayName: "user",
            ChannelId: 2,
            ChannelName: "channel",
            Persona: "Robotnik",
            InvocationKind: "Ambient",
            Prompt: "prompt",
            Reply: "reply",
            EpisodeId: EpisodeId,
            TriggerMessageId: TriggerMessageId,
            ReplyTargetMessageId: ReplyTargetMessageId,
            EvidenceDigest: EvidenceDigest);

    private async Task<string> ReadOnlyLineAsync()
    {
        var path = Assert.Single(Directory.EnumerateFiles(_tempDir, "transcript-*.jsonl"));
        return (await File.ReadAllTextAsync(path)).TrimEnd('\n');
    }
}