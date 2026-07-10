using DiscordSky.Bot.Memory.Reception;

namespace DiscordSky.Tests;

public class SentMessageRegistryTests
{
    [Fact]
    public void Register_TryGet_PreservesPersonaSourceAndTrigger()
    {
        var now = new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
        var registry = new SentMessageRegistry(() => now);

        registry.Register(42, "Robotnik", "cold_open", 17);

        Assert.True(registry.TryGet(42, out var sent));
        Assert.Equal("Robotnik", sent.Persona);
        Assert.Equal("cold_open", sent.Source);
        Assert.Equal(17UL, sent.TriggerMessageId);
        Assert.Equal(now, sent.CreatedAt);
    }

    [Fact]
    public void Register_ReplacesExistingMetadataForSameMessage()
    {
        var registry = new SentMessageRegistry();
        registry.Register(42, "Robotnik", "reply");
        registry.Register(42, "a stern wizard", "test");

        Assert.True(registry.TryGet(42, out var sent));
        Assert.Equal("a stern wizard", sent.Persona);
        Assert.Equal("test", sent.Source);
        Assert.Equal(1, registry.Count);
    }
}
