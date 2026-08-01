using System.Text.Json;
using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class StewardCapabilitiesSnapshotTests
{
    [Fact]
    public void Parse_AcceptsAnExactUnrestrictedGuildBinding()
    {
        using var document = JsonDocument.Parse("""
            {
              "guildId": "667956000757776386",
              "profile": "funhouse",
              "profileDigest": "profile-digest",
              "authorizationMode": "UnrestrictedAutonomy",
              "mode": "unrestricted",
              "manifestDigest": "manifest-digest",
              "registeredTools": ["get_guild", "update_channel"],
              "policy": {
                "protectedResourceCount": 0,
                "protectedNamePrefixCount": 0,
                "deniedPermissionCount": 0
              }
            }
            """);

        var snapshot = StewardCapabilitiesSnapshot.Parse(document.RootElement, 667956000757776386);

        Assert.Equal("funhouse", snapshot.Profile);
        Assert.Equal(["get_guild", "update_channel"], snapshot.RegisteredTools.ToArray());
    }

    [Theory]
    [InlineData("InteractiveApproval", "operator", 0, 0, 0)]
    [InlineData("UnrestrictedAutonomy", "unrestricted", 1, 0, 0)]
    [InlineData("UnrestrictedAutonomy", "unrestricted", 0, 1, 0)]
    [InlineData("UnrestrictedAutonomy", "unrestricted", 0, 0, 1)]
    public void Parse_RejectsNonUnrestrictedOrLocallyProtectedCapabilities(
        string authorizationMode,
        string mode,
        int protectedResources,
        int protectedPrefixes,
        int deniedPermissions)
    {
        using var document = JsonDocument.Parse($$"""
            {
              "guildId": "667956000757776386",
              "profile": "funhouse",
              "profileDigest": "profile-digest",
              "authorizationMode": "{{authorizationMode}}",
              "mode": "{{mode}}",
              "manifestDigest": "manifest-digest",
              "registeredTools": ["get_guild"],
              "policy": {
                "protectedResourceCount": {{protectedResources}},
                "protectedNamePrefixCount": {{protectedPrefixes}},
                "deniedPermissionCount": {{deniedPermissions}}
              }
            }
            """);

        Assert.Throws<InvalidOperationException>(() =>
            StewardCapabilitiesSnapshot.Parse(document.RootElement, 667956000757776386));
    }

    [Fact]
    public void Parse_RejectsAChildBoundToAnotherGuild()
    {
        using var document = JsonDocument.Parse("""
            {
              "guildId": "667956000757776387",
              "profile": "funhouse",
              "profileDigest": "profile-digest",
              "authorizationMode": "UnrestrictedAutonomy",
              "mode": "unrestricted",
              "manifestDigest": "manifest-digest",
              "registeredTools": ["get_guild"],
              "policy": {
                "protectedResourceCount": 0,
                "protectedNamePrefixCount": 0,
                "deniedPermissionCount": 0
              }
            }
            """);

        Assert.Throws<InvalidOperationException>(() =>
            StewardCapabilitiesSnapshot.Parse(document.RootElement, 667956000757776386));
    }
}