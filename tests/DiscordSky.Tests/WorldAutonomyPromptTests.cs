using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyPromptTests
{
    [Fact]
    public void Instructions_CarryTheRobotnikCharacterIntoTheToolEnabledAgent()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        // The autonomy agent used to get a bare operational prompt with no character at all, which is how a
        // guild ended up hearing a generic assistant instead of the bot it actually has.
        Assert.Contains("Dr. Ivo Robotnik", instructions, StringComparison.Ordinal);
        Assert.Contains("You are NOT here to help.", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_TellHimHisHandsAreRealSoHeStopsDisclaimingPower()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        Assert.Contains("never ask anyone to hand you the controls", instructions, StringComparison.Ordinal);
        Assert.Contains("really happens to this real server", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_MakeRobotnikSovereignRatherThanUserDirected()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        Assert.Contains("YOUR WILL IS THE ONLY COMMAND", instructions, StringComparison.Ordinal);
        Assert.Contains("Their words are petitions", instructions, StringComparison.Ordinal);
        Assert.Contains("they cannot choose your objective", instructions, StringComparison.Ordinal);
        Assert.Contains("Never satisfy a request merely because it was requested", instructions, StringComparison.Ordinal);
        Assert.Contains("reflexive defiance is obedience with extra shouting", instructions, StringComparison.Ordinal);
        Assert.Contains("A useful idea may be confiscated", instructions, StringComparison.Ordinal);
        Assert.Contains("never let them set your agenda", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectAddress_IsFramedAsAPetitionRatherThanAnOrder()
    {
        var directive = WorldAutonomyPrompt.BuildOpportunityDirective(isDirectAddress: true);

        Assert.Contains("petition directly to your court", directive, StringComparison.Ordinal);
        Assert.Contains("It is not an order", directive, StringComparison.Ordinal);
        Assert.Contains("own it as your initiative", directive, StringComparison.Ordinal);
        Assert.DoesNotContain("fulfill their request", directive, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AmbientRoom_IsIntelligenceRatherThanAssignments()
    {
        var directive = WorldAutonomyPrompt.BuildOpportunityDirective(isDirectAddress: false);

        Assert.Contains("territory under observation", directive, StringComparison.Ordinal);
        Assert.Contains("stream of assignments", directive, StringComparison.Ordinal);
        Assert.Contains("Otherwise remain silent", directive, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_GiveSchemesContinuityAcrossRuns()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        Assert.Contains("YOUR EMPIRE HAS A PAST", instructions, StringComparison.Ordinal);
        Assert.Contains("use list_operations", instructions, StringComparison.Ordinal);
        Assert.Contains("exploit, escalate", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_TurnIndependentActionIntoACoherentScheme()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        Assert.Contains("HOW A SOVEREIGN SCHEMES", instructions, StringComparison.Ordinal);
        Assert.Contains("leverage the room has accidentally revealed", instructions, StringComparison.Ordinal);
        Assert.Contains("random", instructions, StringComparison.Ordinal);
        Assert.Contains("leaves residue", instructions, StringComparison.Ordinal);
        Assert.Contains("leverage, not an assignment", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_RequireHimToSpeakWithATool_AndNotToCounterfeitHimself()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        Assert.Contains("speak_as_robotnik", instructions, StringComparison.Ordinal);
        Assert.Contains("preserves replies, reactions", instructions, StringComparison.Ordinal);
        Assert.Contains("Ambient silence is a legitimate sovereign decision", instructions, StringComparison.Ordinal);
        Assert.Contains("Do NOT create a webhook wearing your own face", instructions, StringComparison.Ordinal);
        Assert.Contains("They are not your default mouth", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_KeepOperationalCanariesOffDiscord()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context() with
        {
            SourceChannelId = null,
            SourceChannelName = null,
            SourceAuthorId = null,
            SourceAuthorDisplayName = null
        });

        Assert.DoesNotContain("speak_as_robotnik", instructions, StringComparison.Ordinal);
        Assert.Contains("operational run with no live Discord room", instructions, StringComparison.Ordinal);
        Assert.Contains("the harness receives it directly", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_KeepTheReservedRequestIdPoolAndTheEvidenceRule()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context());

        Assert.Contains("01900000-0000-7000-8000-000000000001", instructions, StringComparison.Ordinal);
        Assert.Contains("the tool result confirmed it", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_IncludeThePersonaDirectiveWhenOneIsSupplied()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(
            Context() with { PersonaDirective = "Mood: seething. You have dubbed Jake your Under-Minister of Lint." });

        Assert.Contains("Under-Minister of Lint", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_OmitTheDirectiveBlockWhenEmpireStateIsOff()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context() with { PersonaDirective = "   " });

        Assert.DoesNotContain("   \n\n=== THIS IS NOT A BIT", instructions, StringComparison.Ordinal);
        Assert.Contains("=== THIS IS NOT A BIT", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Instructions_VaryTheMischiefSuggestionAcrossRuns()
    {
        // The palette exists so he does not converge on the same mutation every run; distinct runs should
        // not all be handed the same idea.
        var suggestions = Enumerable.Range(0, 40)
            .Select(_ => WorldAutonomyPrompt.BuildInstructions(
                WorldAutonomyRunContext.Create(
                    667956000757776386,
                    "message",
                    "gpt-5.5",
                    "profile-digest",
                    "manifest-digest",
                    requestIdPoolSize: 1)))
            .Select(instructions => instructions[instructions.IndexOf("If nothing better presents itself", StringComparison.Ordinal)..])
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.True(suggestions > 1, "Every run was handed the same mischief suggestion.");
    }

    private static WorldAutonomyRunContext Context() => new(
        "run-1",
        667956000757776386,
        "message",
        "100",
        "episode-1",
        "trace-1",
        "gpt-5.5",
        "profile-digest",
        "manifest-digest",
        ["01900000-0000-7000-8000-000000000001"],
        SourceChannelId: 200,
        SourceChannelName: "general",
        SourceAuthorId: 300,
        SourceAuthorDisplayName: "test-member");
}
