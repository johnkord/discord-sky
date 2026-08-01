using System.Text.Json;
using DiscordSky.Bot.Integrations.Reactions;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class ReactionCapabilityRegistryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "discord-sky-reaction-capability-" + Guid.NewGuid().ToString("N"));

    public ReactionCapabilityRegistryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExactBlock_PersistsAcrossRestartAndClearRemovesIt()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var first = Build(() => now);
        await first.StartAsync(CancellationToken.None);

        var recorded = first.RecordExactBlock(1, 2, 90_001);

        Assert.NotNull(recorded);
        Assert.Equal(1, recorded!.FailureCount);
        Assert.Equal(now.AddHours(24), recorded.ExpiresAt);
        Assert.True(first.TryGetActive(1, 2, out _));
        var second = Build(() => now.AddHours(1));
        await second.StartAsync(CancellationToken.None);
        Assert.True(second.TryGetActive(1, 2, out var restored));
        Assert.Equal(90_001, restored.DiscordCode);

        Assert.True(second.Clear(1, 2));
        Assert.False(second.TryGetActive(1, 2, out _));
        var third = Build(() => now.AddHours(2));
        await third.StartAsync(CancellationToken.None);
        Assert.False(third.TryGetActive(1, 2, out _));
    }

    [Fact]
    public async Task GenericForbiddenAndOtherCodesNeverCreateBlock()
    {
        var registry = Build(() => DateTimeOffset.UtcNow);
        await registry.StartAsync(CancellationToken.None);

        Assert.Null(registry.RecordExactBlock(1, 2, 50_013));
        Assert.Null(registry.RecordExactBlock(1, 2, 0));
        Assert.False(registry.TryGetActive(1, 2, out _));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ExpiryIsAuthoritativeAndPrunedFromDisk()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var registry = Build(() => now);
        await registry.StartAsync(CancellationToken.None);
        registry.RecordExactBlock(1, 2, 90_001);
        now = now.AddHours(25);

        Assert.False(registry.TryGetActive(1, 2, out _));
        Assert.Equal(0, registry.Count);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(StorePath));
        Assert.Empty(document.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task RepeatedExactFailureRefreshesExpiryAndIncrementsCount()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var registry = Build(() => now);
        await registry.StartAsync(CancellationToken.None);
        registry.RecordExactBlock(1, 2, 90_001);
        now = now.AddHours(2);

        var refreshed = registry.RecordExactBlock(1, 2, 90_001);

        Assert.Equal(2, refreshed?.FailureCount);
        Assert.Equal(now.AddHours(24), refreshed?.ExpiresAt);
    }

    [Fact]
    public async Task CorruptFileAndWriteFailureFailOpen()
    {
        await File.WriteAllTextAsync(StorePath, "not-json");
        var writes = 0;
        var registry = Build(
            () => DateTimeOffset.UtcNow,
            (_, _, _) =>
            {
                writes++;
                throw new IOException("disk unavailable");
            });

        await registry.StartAsync(CancellationToken.None);
        var state = registry.RecordExactBlock(1, 2, 90_001);

        Assert.Equal(1, registry.Count);
        Assert.NotNull(state);
        Assert.True(registry.TryGetActive(1, 2, out _));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task DisabledRegistryNeverLoadsOrBlocks()
    {
        var registry = new FileBackedReactionCapabilityRegistry(
            new ReactionOptions
            {
                CapabilityCooldownEnabled = false,
                CapabilityStorePath = StorePath,
            },
            NullLogger<FileBackedReactionCapabilityRegistry>.Instance,
            () => DateTimeOffset.UtcNow,
            (_, _, _) => throw new InvalidOperationException("must not write"));

        await registry.StartAsync(CancellationToken.None);

        Assert.Null(registry.RecordExactBlock(1, 2, 90_001));
        Assert.False(registry.TryGetActive(1, 2, out _));
    }

    [Fact]
    public async Task BotVetoHelper_ShortCircuitsWhenDisabledAndReturnsActiveStateWhenEnabled()
    {
        var registry = Build(() => DateTimeOffset.UtcNow);
        await registry.StartAsync(CancellationToken.None);
        registry.RecordExactBlock(1, 2, 90_001);

        Assert.False(DiscordSky.Bot.Bot.DiscordBotService.TryGetActiveReactionBlock(
            enabled: false,
            registry,
            guildId: 1,
            userId: 2,
            out _));
        Assert.True(DiscordSky.Bot.Bot.DiscordBotService.TryGetActiveReactionBlock(
            enabled: true,
            registry,
            guildId: 1,
            userId: 2,
            out var state));
        Assert.Equal(90_001, state.DiscordCode);
    }

    [Fact]
    public async Task RegistryCapsEntriesAndPersistsMetadataOnly()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        string? latestJson = null;
        var registry = Build(
            () => now,
            (_, _, json) => latestJson = json);
        await registry.StartAsync(CancellationToken.None);

        for (ulong userId = 1; userId <= 1_005; userId++)
        {
            registry.RecordExactBlock(1, userId, 90_001);
            now = now.AddSeconds(1);
        }

        Assert.Equal(1_000, registry.Count);
        Assert.False(registry.TryGetActive(1, 1, out _));
        Assert.True(registry.TryGetActive(1, 1_005, out _));
        Assert.NotNull(latestJson);
        Assert.DoesNotContain("message", latestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", latestJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discordCode", latestJson, StringComparison.Ordinal);
    }

    private string StorePath => Path.Combine(_tempDir, "reaction-capabilities.json");

    private FileBackedReactionCapabilityRegistry Build(
        Func<DateTimeOffset> clock,
        Action<string, string, string>? writer = null) => new(
            new ReactionOptions
            {
                CapabilityCooldownEnabled = true,
                BlockedUserCooldownHours = 24,
                CapabilityStorePath = StorePath,
            },
            NullLogger<FileBackedReactionCapabilityRegistry>.Instance,
            clock,
            writer ?? ((tempPath, path, json) =>
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }));
}