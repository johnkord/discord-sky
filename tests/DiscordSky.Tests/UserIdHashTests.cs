using DiscordSky.Bot.Memory.Logging;

namespace DiscordSky.Tests;

public class UserIdHashTests
{
    [Fact]
    public void Hash_IsStableAcrossCalls()
    {
        Assert.Equal(UserIdHash.Hash(12345UL), UserIdHash.Hash(12345UL));
    }

    [Fact]
    public void Hash_DiffersBetweenIds()
    {
        Assert.NotEqual(UserIdHash.Hash(1UL), UserIdHash.Hash(2UL));
    }

    [Fact]
    public void Hash_Is10HexChars()
    {
        var h = UserIdHash.Hash(999UL);
        Assert.Equal(10, h.Length);
        Assert.Matches("^[0-9a-f]{10}$", h);
    }
}
