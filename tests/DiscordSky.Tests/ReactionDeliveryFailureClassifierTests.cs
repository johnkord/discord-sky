using DiscordSky.Bot.Integrations.Reactions;

namespace DiscordSky.Tests;

public sealed class ReactionDeliveryFailureClassifierTests
{
    [Fact]
    public void ExactReactionBlockedCode_IsCapabilityBlock()
    {
        var failure = ReactionDeliveryFailureClassifier.Classify(403, 90_001);

        Assert.Equal("reaction_blocked", failure.ReasonCode);
        Assert.True(failure.IsCapabilityBlock);
        Assert.False(failure.IsTransient);
    }

    [Fact]
    public void GenericForbidden_IsNotCapabilityBlock()
    {
        var failure = ReactionDeliveryFailureClassifier.Classify(403, null);

        Assert.Equal("other_http", failure.ReasonCode);
        Assert.False(failure.IsCapabilityBlock);
    }

    [Theory]
    [InlineData(404, 10_008, "unknown_message", false)]
    [InlineData(400, 10_014, "unknown_emoji", false)]
    [InlineData(403, 50_013, "missing_permissions", false)]
    [InlineData(429, null, "rate_limited", true)]
    [InlineData(503, null, "transient_transport", true)]
    public void KnownFailures_AreClassified(
        int httpStatus,
        int? discordCode,
        string expectedReason,
        bool expectedTransient)
    {
        var failure = ReactionDeliveryFailureClassifier.Classify(httpStatus, discordCode);

        Assert.Equal(expectedReason, failure.ReasonCode);
        Assert.Equal(expectedTransient, failure.IsTransient);
        Assert.False(failure.IsCapabilityBlock);
    }

    [Fact]
    public void TransportExceptionWithoutHttpStatus_IsTransient()
    {
        var failure = ReactionDeliveryFailureClassifier.Classify(null, null, isTransportFailure: true);

        Assert.Equal("transient_transport", failure.ReasonCode);
        Assert.True(failure.IsTransient);
    }

    [Fact]
    public void UnexpectedException_IsNotConflatedWithHttpFailure()
    {
        var failure = ReactionDeliveryFailureClassifier.Unexpected();

        Assert.Equal("unexpected_exception", failure.ReasonCode);
        Assert.Null(failure.HttpStatus);
        Assert.Null(failure.DiscordCode);
        Assert.False(failure.IsCapabilityBlock);
    }
}