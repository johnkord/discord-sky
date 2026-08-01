using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public sealed class ImagePromptProjectionTests
{
    [Fact]
    public void TrumpsBallroomPizzaFixture_OmitsUnreferencedOperationalMetadata()
    {
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var request = new CreativeRequest(
            "Robotnik from AOSTH",
            "Make this pizza look like a hostile takeover.",
            "Alice",
            1,
            2,
            3,
            now,
            CreativeInvocationKind.DirectReply,
            TriggerMessageId: 9001,
            Channel: new ChannelContext(
                "general",
                "pizza chat",
                "Robotnik Test Guild",
                false,
                "dinner-plans",
                487,
                31,
                now.AddMinutes(-4)));
        const string modelTreatment =
            "A pizza banquet in Robotnik Test Guild inside the Discord channel #general, watched by 487 server members in a busy Discord channel. Bot last spoke four minutes ago. Robotnik stamps the pie with an imperial seal.";

        var projection = ImagePromptProjectionBuilder.Build(
            request,
            Array.Empty<ChannelMessage>(),
            modelTreatment,
            Array.Empty<ulong>());

        Assert.Contains("pizza", projection.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Robotnik stamps the pie", projection.Prompt);
        Assert.DoesNotContain("Robotnik Test Guild", projection.Prompt);
        Assert.DoesNotContain("#general", projection.Prompt);
        Assert.DoesNotContain("487", projection.Prompt);
        Assert.DoesNotContain("busy Discord channel", projection.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bot last spoke", projection.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("watched by people", projection.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the room", projection.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new ulong[] { 9001 }, projection.EvidenceMessageIds);
        Assert.Equal(64, projection.PromptDigest.Length);
    }

    [Fact]
    public void ExplicitlyRequestedServerName_RemainsSubjectEvidence()
    {
        var request = new CreativeRequest(
            "Robotnik",
            "Put Robotnik Test Guild on the pizza box.",
            "Alice",
            1,
            2,
            3,
            DateTimeOffset.UtcNow,
            Channel: new ChannelContext("general", null, "Robotnik Test Guild", false, null, 10, 1, null));

        var projection = ImagePromptProjectionBuilder.Build(
            request,
            Array.Empty<ChannelMessage>(),
            "A pizza box labeled Robotnik Test Guild.",
            Array.Empty<ulong>());

        Assert.Contains("Robotnik Test Guild", projection.Prompt);
    }
}