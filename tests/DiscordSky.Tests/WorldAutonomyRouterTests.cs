using DiscordSky.Bot.Orchestration.Autonomy;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyRouterTests
{
    [Fact]
    public async Task Router_SerializesOverlappingOpportunitiesForOneBoundGuild()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);
        var opportunity = new WorldAutonomyOpportunity(
            667956000757776386,
            "discord_message",
            "The room has begun arguing about magnetism.");

        var first = router.TryRunAsync(opportunity, CancellationToken.None);
        await runner.WaitForStartAsync(0);
        await router.TryRunAsync(opportunity with { Prompt = "A second message." }, CancellationToken.None);

        Assert.Equal(1, runner.CallCount);
        runner.Release(0);
        await runner.WaitForStartAsync(1);
        runner.Release(1);
        await first;

        Assert.Equal([opportunity.Prompt, "A second message."], runner.Prompts);
    }

    [Fact]
    public async Task Router_IgnoresAnUnboundGuild()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);

        await router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776387, "discord_message", "No binding."),
            CancellationToken.None);

        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task Router_CoalescesAmbientChatterToTheNewestRoomState()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);
        var opportunity = new WorldAutonomyOpportunity(667956000757776386, "discord_message", "First.");

        var first = router.TryRunAsync(opportunity, CancellationToken.None);
        await runner.WaitForStartAsync(0);
        await router.TryRunAsync(opportunity with { Prompt = "Superseded." }, CancellationToken.None);
        await router.TryRunAsync(opportunity with { Prompt = "Newest." }, CancellationToken.None);

        runner.Release(0);
        await runner.WaitForStartAsync(1);
        runner.Release(1);
        await first;

        Assert.Equal(["First.", "Newest."], runner.Prompts);
    }

    [Fact]
    public async Task Router_AnswersADirectAddressWhenTheGuildIsIdle()
    {
        var runner = new SequencedRunner();
        runner.Release(0);
        var router = Router(runner);

        var result = await router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "Rename some channels.",
                IsDirectAddress: true),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("succeeded", result!.Status);
    }

    [Fact]
    public async Task Router_KeepsABusyDirectAddressOwnedUntilItsRunCompletes()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);
        var first = router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Mid-scheme."),
            CancellationToken.None);
        await runner.WaitForStartAsync(0);

        var direct = router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "Answer me.",
                IsDirectAddress: true),
            CancellationToken.None);

        Assert.False(direct.IsCompleted);
        runner.Release(0);
        await runner.WaitForStartAsync(1);
        Assert.False(direct.IsCompleted);
        runner.Release(1);

        var result = await direct;
        await first;

        Assert.NotNull(result);
        Assert.Equal(["Mid-scheme.", "Answer me."], runner.Prompts);
    }

    [Fact]
    public async Task Router_PreservesDirectFifoAheadOfCoalescedAmbientChatter()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);
        var first = router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "First ambient."),
            CancellationToken.None);
        await runner.WaitForStartAsync(0);

        var firstDirect = router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "First audience.",
                IsDirectAddress: true),
            CancellationToken.None);
        var secondDirect = router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "Second audience.",
                IsDirectAddress: true),
            CancellationToken.None);
        await router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Stale ambient."),
            CancellationToken.None);
        await router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Latest ambient."),
            CancellationToken.None);

        for (var index = 0; index < 4; index++)
        {
            runner.Release(index);
            if (index < 3)
            {
                await runner.WaitForStartAsync(index + 1);
            }
        }

        await Task.WhenAll(first, firstDirect, secondDirect);

        Assert.Equal(
            ["First ambient.", "First audience.", "Second audience.", "Latest ambient."],
            runner.Prompts);
    }

    [Fact]
    public async Task Router_ContinuesTheMailboxAfterARunThrows()
    {
        var runner = new SequencedRunner();
        runner.Fail(0, new InvalidOperationException("The first scheme exploded."));
        var router = Router(runner);

        var first = router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Doomed."),
            CancellationToken.None);
        await runner.WaitForStartAsync(0);
        var direct = router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "Still waiting.",
                IsDirectAddress: true),
            CancellationToken.None);

        runner.Release(0);
        await runner.WaitForStartAsync(1);
        runner.Release(1);

        await first;
        var result = await direct;

        Assert.NotNull(result);
        Assert.Equal(["Doomed.", "Still waiting."], runner.Prompts);
    }

    [Fact]
    public async Task Router_IgnoresADirectAddressInAnUnboundGuild()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);

        var result = await router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776387,
                "discord_message",
                "No binding.",
                IsDirectAddress: true),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task Router_StartsAdmittedAmbientWithoutASecondDebounce()
    {
        var runner = new SequencedRunner();
        var router = Router(runner, coalescingEnabled: true, episodeWindowMilliseconds: 5000);

        var worker = router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Admitted episode."),
            CancellationToken.None);
        await runner.WaitForStartAsync(0, TimeSpan.FromMilliseconds(500));
        runner.Release(0);
        await worker;

        Assert.Equal(["Admitted episode."], runner.Prompts);
    }

    [Fact]
    public async Task Router_DirectAudienceRemovesAmbientWaitingBehindActiveRun()
    {
        var runner = new SequencedRunner();
        var router = Router(runner);
        var active = router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Active ambient."),
            CancellationToken.None);
        await runner.WaitForStartAsync(0);
        await router.TryRunAsync(
            new WorldAutonomyOpportunity(667956000757776386, "discord_message", "Waiting ambient."),
            CancellationToken.None);

        var direct = router.TryRunDirectAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "Direct petition.",
                IsDirectAddress: true),
            CancellationToken.None);

        runner.Release(0);
    await runner.WaitForStartAsync(1);
    runner.Release(1);
    await Task.WhenAll(active, direct);

    Assert.Equal(["Active ambient.", "Direct petition."], runner.Prompts);
    }

    [Fact]
    public async Task Router_DeliveredSpeechActivatesPostSpeechGuard()
    {
        var runner = new SequencedRunner();
        runner.Speak(0);
        runner.Release(0);
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            AmbientPostSpeechGuardEnabled = true,
            AmbientPostSpeechHumanTurns = 2,
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["667956000757776386"] = new() { ProfilePath = "profile.json" }
            }
        });
        var guard = new WorldAutonomyPostSpeechGuard(configuration);
        var router = new WorldAutonomyRouter(
            configuration,
            runner,
            NullLogger<WorldAutonomyRouter>.Instance,
            guard);

        await router.TryRunAsync(
            new WorldAutonomyOpportunity(
                667956000757776386,
                "discord_message",
                "A worthy opening.",
                SourceChannelId: 6001),
            CancellationToken.None);

        var followUp = guard.ObserveAmbient(667956000757776386, 6001, "lol", hasMedia: false);
        Assert.False(followUp.Allowed);
        Assert.Equal("post_speech_waiting", followUp.Reason);
    }

    [Fact]
    public void Router_ExternalFallbackDeliveryActivatesPostSpeechGuard()
    {
        var configuration = WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            AmbientPostSpeechGuardEnabled = true,
            AmbientPostSpeechHumanTurns = 2,
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["667956000757776386"] = new() { ProfilePath = "profile.json" }
            }
        });
        var guard = new WorldAutonomyPostSpeechGuard(configuration);
        var router = new WorldAutonomyRouter(
            configuration,
            new SequencedRunner(),
            NullLogger<WorldAutonomyRouter>.Instance,
            guard);

        router.RecordDeliveredSpeech(667956000757776386, 6001);

        var followUp = guard.ObserveAmbient(667956000757776386, 6001, "lol", hasMedia: false);
        Assert.False(followUp.Allowed);
    }

    private static WorldAutonomyRouter Router(
        IWorldAutonomyRunner runner,
        bool coalescingEnabled = false,
        int episodeWindowMilliseconds = 1500) => new(
        EnabledConfiguration(coalescingEnabled, episodeWindowMilliseconds),
        runner,
        NullLogger<WorldAutonomyRouter>.Instance);

    private static WorldAutonomyConfiguration EnabledConfiguration(
        bool coalescingEnabled = false,
        int episodeWindowMilliseconds = 1500) =>
        WorldAutonomyConfiguration.FromOptions(new WorldAutonomyOptions
        {
            StewardCommand = "dotnet",
            StewardArguments = ["/app/steward/DiscordSteward.dll"],
            AmbientEpisodeCoalescingEnabled = coalescingEnabled,
            AmbientEpisodeWindowMilliseconds = episodeWindowMilliseconds,
            EnabledGuilds = new Dictionary<string, WorldAutonomyGuildOptions>
            {
                ["667956000757776386"] = new() { ProfilePath = "/app/steward/profiles/funhouse.json" }
            }
        });

    private sealed class SequencedRunner : IWorldAutonomyRunner
    {
        private const int Capacity = 8;
        private readonly List<string> _prompts = [];
        private readonly TaskCompletionSource[] _started = Enumerable.Range(0, Capacity)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private readonly TaskCompletionSource[] _release = Enumerable.Range(0, Capacity)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        private readonly Exception?[] _failures = new Exception?[Capacity];
        private readonly bool[] _speaks = new bool[Capacity];
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal IReadOnlyList<string> Prompts
        {
            get
            {
                lock (_prompts)
                {
                    return _prompts.ToArray();
                }
            }
        }

        internal Task WaitForStartAsync(int index, TimeSpan? timeout = null) =>
            _started[index].Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

        internal void Release(int index) => _release[index].TrySetResult();

        internal void Fail(int index, Exception exception) => _failures[index] = exception;

        internal void Speak(int index) => _speaks[index] = true;

        public async Task<WorldAutonomyRunResult> RunAsync(
            WorldAutonomyOpportunity opportunity,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            lock (_prompts)
            {
                _prompts.Add(opportunity.Prompt);
            }

            _started[index].TrySetResult();
            await _release[index].Task.WaitAsync(cancellationToken);
            if (_failures[index] is not null)
            {
                throw _failures[index]!;
            }

            return new WorldAutonomyRunResult(
                $"run-{index + 1}",
                opportunity.GuildId,
                "succeeded",
                FinalText: null,
                FailureReason: null,
                SpokeInChannel: _speaks[index]);
        }
    }
}