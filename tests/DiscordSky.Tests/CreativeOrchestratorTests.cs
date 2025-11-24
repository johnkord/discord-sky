using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;

namespace DiscordSky.Tests;

public class CreativeOrchestratorTests
{
    [Fact]
    public void BuildEmptyResponsePlaceholder_CommandInvocation_ReturnsPersonaNotice()
    {
        var placeholder = CreativeOrchestrator.BuildEmptyResponsePlaceholder("Robotnik from AOSTH", CreativeInvocationKind.Command);
        Assert.Equal("[Robotnik from AOSTH pauses dramatically but says nothing.]", placeholder);
    }

    [Fact]
    public void BuildEmptyResponsePlaceholder_AmbientInvocation_ReturnsEmpty()
    {
        var placeholder = CreativeOrchestrator.BuildEmptyResponsePlaceholder("Robotnik from AOSTH", CreativeInvocationKind.Ambient);
        Assert.Equal(string.Empty, placeholder);
    }
}
