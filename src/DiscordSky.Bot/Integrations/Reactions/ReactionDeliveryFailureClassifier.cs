namespace DiscordSky.Bot.Integrations.Reactions;

public sealed record ReactionDeliveryFailure(
    string ReasonCode,
    int? HttpStatus,
    int? DiscordCode,
    bool IsCapabilityBlock,
    bool IsTransient);

public static class ReactionDeliveryFailureClassifier
{
    public const int ReactionBlockedCode = 90_001;
    public const int UnknownMessageCode = 10_008;
    public const int UnknownEmojiCode = 10_014;
    public const int MissingPermissionsCode = 50_013;

    public static ReactionDeliveryFailure Classify(
        int? httpStatus,
        int? discordCode,
        bool isTransportFailure = false)
    {
        if (discordCode == ReactionBlockedCode)
        {
            return new ReactionDeliveryFailure(
                "reaction_blocked", httpStatus, discordCode, IsCapabilityBlock: true, IsTransient: false);
        }

        var reasonCode = discordCode switch
        {
            UnknownMessageCode => "unknown_message",
            UnknownEmojiCode => "unknown_emoji",
            MissingPermissionsCode => "missing_permissions",
            _ when httpStatus == 429 => "rate_limited",
            _ when isTransportFailure || httpStatus is >= 500 and <= 599 => "transient_transport",
            _ => "other_http",
        };
        var transient = reasonCode is "rate_limited" or "transient_transport";
        return new ReactionDeliveryFailure(
            reasonCode, httpStatus, discordCode, IsCapabilityBlock: false, transient);
    }

    public static ReactionDeliveryFailure Unexpected() => new(
        "unexpected_exception", null, null, IsCapabilityBlock: false, IsTransient: false);
}