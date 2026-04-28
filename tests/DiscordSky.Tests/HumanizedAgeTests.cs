using DiscordSky.Bot.Memory;

namespace DiscordSky.Tests;

public class HumanizedAgeTests
{
    [Fact]
    public void JustNow_ForSubMinute()
    {
        Assert.Equal("just now", HumanizedAge.Format(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Minutes_UnderAnHour()
    {
        Assert.Equal("5 minutes ago", HumanizedAge.Format(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Hours_UnderADay()
    {
        Assert.Equal("3 hours ago", HumanizedAge.Format(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void Days_UnderTwoWeeks()
    {
        Assert.Equal("5 days ago", HumanizedAge.Format(TimeSpan.FromDays(5)));
    }

    [Fact]
    public void Weeks_UnderTwoMonths()
    {
        Assert.Equal("3 weeks ago", HumanizedAge.Format(TimeSpan.FromDays(21)));
    }

    [Fact]
    public void Months_UnderAYear()
    {
        Assert.Equal("4 months ago", HumanizedAge.Format(TimeSpan.FromDays(120)));
    }

    [Fact]
    public void OverAYear()
    {
        Assert.Equal("over a year ago", HumanizedAge.Format(TimeSpan.FromDays(400)));
    }

    [Fact]
    public void NegativeClampsToJustNow()
    {
        Assert.Equal("just now", HumanizedAge.Format(TimeSpan.FromMinutes(-5)));
    }
}
