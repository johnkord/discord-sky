using Discord;
using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyPromptTests
{
    [Theory]
    [InlineData("Appeal: the seal is upside down", false)]
    [InlineData("<@42> appeal on the record: <@111> objects", true)]
    [InlineData("OBJECTION! That's my Thursday.", false)]
    [InlineData("appeal", false)]
    public void CourtAcceptsOnlyAnInvitedReplyToTheSelectedBotMessage(string text, bool onRecord)
    {
        var reference = new MessageReference(7001, 5001);
        var exhibit = new AppealExhibit(7001, 5001, 42, "Thursday now answers to me.");
        var appeal = DecreeAppeal.TryOpen(text, MessageType.Reply, reference, 4001, 5001, 42, exhibit);
        Assert.NotNull(appeal);
        Assert.Equal(onRecord, appeal!.OnRecord);
        Assert.Contains(exhibit.Text, appeal.Brief(null));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Default, reference, 4001, 5001, 42, exhibit));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply, reference, 4001, 5001, 42, exhibit with { AuthorId = 111 }));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply, reference, 4001, 5001, 42, exhibit with { MessageId = 7002 }));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply, reference, 4001, 5001, 42, exhibit with { ChannelId = 5002 }));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply, reference, 4001, 5001, 42, null));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply, new MessageReference(7001, 5001, 4002), 4001, 5001, 42, exhibit));
        Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply, new MessageReference(7001, 5001, 4001, null, MessageReferenceType.Forward), 4001, 5001, 42, exhibit));
    }

    [Theory]
    [InlineData("That was an appealing idea.")]
    [InlineData("I saw an appeal: it was denied.")]
    [InlineData("appeal to authority")]
    [InlineData("objectionable")]
    [InlineData("appeal on the record says what?")]
    public void OrdinaryConversationIsNotACourtFiling(string text) => Assert.Null(DecreeAppeal.TryOpen(text, MessageType.Reply,
        new MessageReference(7001, 5001), 4001, 5001, 42, new(7001, 5001, 42, "A ruling.")));

    [Fact]
    public void CourtExhibitsAreBoundedQuotedTextNotLinksOrPrivateReceipts()
    {
        var appeal = DecreeAppeal.TryOpen("appeal: reconsider", MessageType.Reply, new MessageReference(7001, 5001), 4001, 5001, 42,
            new(7001, 5001, 42, "The\nline https://example.invalid/private?signature=fixture \"objection\" " + new string('s', 620)))!;
        Assert.True(appeal.Excerpted);
        Assert.Equal(DecreeAppeal.QuoteLimit, appeal.Quotation.Length);
        var deed = new VerifiedDeed("set_nickname", "111", "Unpublished", DateTimeOffset.UnixEpoch, "private-id");
        var brief = appeal.Brief(deed);
        Assert.DoesNotContain("signature=", brief);
        Assert.DoesNotContain("Unpublished", brief);
        Assert.DoesNotContain("private-id", brief);
        Assert.Contains("spoken ruling only", (appeal with { OnRecord = true }).Brief(deed, speechOnly: true));
        Assert.DoesNotContain("Unpublished", (appeal with { OnRecord = true }).Brief(deed, speechOnly: true));
    }

    [Fact]
    public async Task OptionalEvidenceIsNeverReadOnOrdinaryOrAmbiguousFilings()
    {
        var reader = new UnavailableRecords();
        var appeal = new DecreeAppeal(4001, 5001, 7001, "<@111> is Copper", false, "<@111> objects", false);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("quote_only", (await appeal.PrepareAsync(reader, now, default)).Outcome);
        Assert.Equal("no_named_subject", (await (appeal with { OnRecord = true, Grounds = "this is unjust" }).PrepareAsync(reader, now, default)).Outcome);
        Assert.Equal("excerpt_only", (await (appeal with { OnRecord = true, Excerpted = true }).PrepareAsync(reader, now, default)).Outcome);
        Assert.Equal(0, reader.Calls);
        var unavailable = await (appeal with { OnRecord = true }).PrepareAsync(reader, now, default);
        Assert.Equal("evidence_unavailable", unavailable.Outcome);
        Assert.Contains("does not prove nothing happened", unavailable.Prompt);
        Assert.Equal(1, reader.Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => appeal.PrepareAsync(reader, now, new CancellationToken(true)));
    }

    private sealed class UnavailableRecords : IVerifiedDeedReader
    {
        public int Calls { get; private set; }
        public Task<VerifiedDeed?> ReadAppealedDeedAsync(ulong guild, ulong channel, ulong messageId, string decree, string petition, DateTimeOffset now, CancellationToken token)
        {
            Calls++;
            throw new IOException("Unavailable fixture");
        }
    }

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
    public void Instructions_UseExplicitFinalSpeechContractWhenTerminalDeliveryIsEnabled()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context(), terminalDeliveryEnabled: true);

        Assert.Contains(WorldAutonomySpeechTool.TerminalToolName, instructions, StringComparison.Ordinal);
        Assert.Contains("Call it alone as your final act", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("with speak_as_robotnik", instructions, StringComparison.Ordinal);
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

    [Fact]
    public void CachePrefix_ContainsOnlyStableRobotnikAndOperationalContracts()
    {
        var stable = WorldAutonomyPrompt.BuildStableCachePrefix(
            hasSourceChannel: true,
            terminalDeliveryEnabled: true);

        Assert.Contains("Dr. Ivo Robotnik", stable, StringComparison.Ordinal);
        Assert.Contains(WorldAutonomySpeechTool.TerminalToolName, stable, StringComparison.Ordinal);
        Assert.Contains("deferred tool search", stable, StringComparison.Ordinal);
        Assert.DoesNotContain("667956000757776386", stable, StringComparison.Ordinal);
        Assert.DoesNotContain("01900000-0000-7000-8000-000000000001", stable, StringComparison.Ordinal);
        Assert.DoesNotContain("Under-Minister", stable, StringComparison.Ordinal);
        Assert.True(stable.Length >= 4096, "Stable prefix may be too short to reach the 1,024-token cache minimum.");
    }

    [Fact]
    public void CacheSuffix_ContainsEveryRunSpecificInstruction()
    {
        var dynamic = WorldAutonomyPrompt.BuildDynamicCacheSuffix(Context() with
        {
            PersonaDirective = "Mood: seething. Under-Minister appointed."
        });

        Assert.Contains("667956000757776386", dynamic, StringComparison.Ordinal);
        Assert.Contains("Under-Minister appointed", dynamic, StringComparison.Ordinal);
        Assert.Contains("01900000-0000-7000-8000-000000000001", dynamic, StringComparison.Ordinal);
        Assert.Contains("Optional inspiration", dynamic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Instructions_RequireOpenAIImagesWithoutRemovingTheChoiceToDecline(bool terminal)
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context(), terminal);
        var cached = WorldAutonomyPrompt.BuildStableCachePrefix(true, terminal);
        foreach (var prompt in new[] { instructions, cached })
        {
            Assert.Contains("use create_visual", prompt);
            Assert.Contains("Flare for new images and Sunburst for reference edits", prompt);
            Assert.Contains("code-block drawings are not alternative media", prompt);
            Assert.Contains("You may decline a drawing in plain text", prompt);
        }
    }

    [Fact]
    public void DirectToolProvider_PromptRetainsAuthorityWithoutAdvertisingHostedSearch()
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context(), true, useHostedToolSearch: false);
        var cached = WorldAutonomyPrompt.BuildStableCachePrefix(true, true, useHostedToolSearch: false);
        foreach (var prompt in new[] { instructions, cached })
        {
            Assert.Contains("exposed directly as function tools", prompt);
            Assert.DoesNotContain("tool search", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("YOUR WILL IS THE ONLY COMMAND", prompt);
            Assert.Contains("use create_visual", prompt);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Instructions_ScopeMutationDenialsWithoutRemovingDiscretion(bool hostedSearch)
    {
        var instructions = WorldAutonomyPrompt.BuildInstructions(Context(), true, hostedSearch);
        var cached = WorldAutonomyPrompt.BuildStableCachePrefix(true, true, hostedSearch);
        foreach (var prompt in new[] { instructions, cached })
        {
            Assert.Contains("reported target and action", prompt);
            Assert.Contains("A guild owner's nickname is protected", prompt);
            Assert.Contains("a role title below your highest role may still be assignable", prompt);
            Assert.Contains("Speaking a title does not edit a Discord nickname", prompt);
            Assert.Contains("You may choose a feasible alternative or decline", prompt);
            Assert.DoesNotContain("never claim you lack permission", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("YOUR WILL IS THE ONLY COMMAND", prompt);
        }
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
