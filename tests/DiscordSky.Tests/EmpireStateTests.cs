using System.Collections.Generic;
using System.IO;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Models.Orchestration;
using DiscordSky.Bot.Orchestration;
using DiscordSky.Bot.Orchestration.Empire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public class EmpireMoodTests
{
    [Fact]
    public void Decay_MovesTowardBaseline()
    {
        var start = EmpireMood.Make(-0.8, 0.9);
        var decayed = EmpireMood.Decay(start, baselineValence: 0.3, baselineArousal: 0.5, retain: 0.5);
        // Halfway to baseline on each axis.
        Assert.Equal(0.3 + (-0.8 - 0.3) * 0.5, decayed.Valence, 3);
        Assert.Equal(0.5 + (0.9 - 0.5) * 0.5, decayed.Arousal, 3);
    }

    [Fact]
    public void Decay_RepeatedConverges()
    {
        var m = EmpireMood.Make(-1.0, 1.0);
        for (var i = 0; i < 50; i++) m = EmpireMood.Decay(m, 0.3, 0.5, 0.7);
        Assert.Equal(0.3, m.Valence, 2);
        Assert.Equal(0.5, m.Arousal, 2);
    }

    [Theory]
    [InlineData(0.9, 0.9, EmpireMood.Gloating)]
    [InlineData(0.9, -0.9, EmpireMood.Smug)]
    [InlineData(-0.9, 0.9, EmpireMood.Seething)]
    [InlineData(-0.9, -0.9, EmpireMood.Sulking)]
    [InlineData(0.0, 0.0, EmpireMood.Scheming)]
    [InlineData(0.1, 0.9, EmpireMood.Scheming)] // valence below threshold falls through to scheming
    public void DeriveLabel_Quadrants(double v, double a, string expected)
    {
        Assert.Equal(expected, EmpireMood.DeriveLabel(v, a));
    }

    [Fact]
    public void Nudge_Clamps()
    {
        var m = EmpireMood.Make(0.9, 0.9);
        var n = EmpireMood.Nudge(m, 0.5, 0.5);
        Assert.Equal(1.0, n.Valence, 3);
        Assert.Equal(1.0, n.Arousal, 3);
    }
}

public class EmpireTickTests
{
    private static EmpireStateOptions Opts() => new();

    private static EmpireState State(Mood mood, params Rank[] ranks) =>
        new(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, mood, ranks, EmpireSeed.Body);

    [Fact]
    public void Advance_DecaysMoodAndAgesRanks()
    {
        var s = State(EmpireMood.Make(-1.0, 1.0), new Rank("A", "Egg-Polisher", 0));
        var (mood, ranks) = EmpireTick.Advance(s, Opts());
        Assert.True(mood.Valence > -1.0); // decayed toward +0.3
        Assert.Single(ranks);
        Assert.Equal(1, ranks[0].IdleTicks);
    }

    [Fact]
    public void Advance_DropsStaleRanks()
    {
        var o = Opts();
        var s = State(EmpireMood.Make(0.3, 0.5), new Rank("Stale", "Old Title", o.RankIdleTicksMax));
        var (_, ranks) = EmpireTick.Advance(s, o);
        Assert.Empty(ranks); // idle would exceed the max after aging
    }

    [Fact]
    public void MergeRankOps_UpsertsAndAdds()
    {
        var o = Opts();
        var existing = new List<Rank> { new("Curlyquote", "Junior Egg-Polisher", 3) };
        var ops = new List<Rank> { new("Curlyquote", "Senior Egg-Polisher", 0), new("Aaron", "Nemesis Emeritus", 0) };
        var merged = EmpireTick.MergeRankOps(existing, ops, o);
        Assert.Equal(2, merged.Count);
        var cq = merged.First(r => r.Name == "Curlyquote");
        Assert.Equal("Senior Egg-Polisher", cq.Title);
        Assert.Equal(0, cq.IdleTicks); // reset on upsert
    }
}

public class EmpireVerifyBodyTests
{
    private static readonly EmpireStateOptions O = new();
    private const string Good = "## The situation now\nA scheme is afoot.\n\n## Lately\n- A defeat, technically.";

    [Fact]
    public void Accepts_GoodBody() => Assert.True(EmpireBodyConsolidator.VerifyBody(Good, EmpireSeed.Body, O));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("## The situation now\nno lately header here")]      // missing a section
    [InlineData("## Lately\n- only lately, no now header")]           // missing a section
    public void Rejects_EmptyOrMissingSection(string body)
        => Assert.False(EmpireBodyConsolidator.VerifyBody(body, EmpireSeed.Body, O));

    [Fact]
    public void Rejects_OverBudget()
    {
        var huge = "## The situation now\n" + new string('x', O.BodyMaxChars) + "\n## Lately\n- y";
        Assert.False(EmpireBodyConsolidator.VerifyBody(huge, EmpireSeed.Body, O));
    }

    [Fact]
    public void Rejects_GuttedBelowRetainFraction()
    {
        var prior = "## The situation now\n" + new string('x', 800) + "\n## Lately\n- " + new string('y', 200);
        var tiny = "## The situation now\nx\n## Lately\n- y"; // far below prior.Length * 0.5
        Assert.False(EmpireBodyConsolidator.VerifyBody(tiny, prior, O));
    }

    [Fact]
    public void Rejects_ControlChars()
        => Assert.False(EmpireBodyConsolidator.VerifyBody("## The situation now\n\u0007bell\n## Lately\n- y", EmpireSeed.Body, O));

    [Fact]
    public void Rejects_InstructionEcho()
        => Assert.False(EmpireBodyConsolidator.VerifyBody("## The situation now\nReturn ONLY a JSON object\n## Lately\n- y", EmpireSeed.Body, O));
}

public class EmpireConsolidatorParseTests
{
    private static readonly EmpireStateOptions O = new();
    private const string Body = "## The situation now\\nScheming intensifies.\\n\\n## Lately\\n- Coconuts failed again.";

    [Fact]
    public void Parse_ExtractsBodyAndCandidateRank()
    {
        var json = "{\"body\":\"" + Body + "\",\"ranks\":[{\"name\":\"curlyquote\",\"title\":\"Junior Egg-Polisher\"}]}";
        var result = EmpireBodyConsolidator.Parse(json, EmpireSeed.Body, new[] { "Curlyquote" }, O);
        Assert.NotNull(result);
        Assert.Contains("Scheming intensifies", result!.Body);
        Assert.Single(result.RankOps);
        Assert.Equal("Curlyquote", result.RankOps[0].Name); // canonical casing from the candidate list
        Assert.Equal("Junior Egg-Polisher", result.RankOps[0].Title);
    }

    [Fact]
    public void Parse_RejectsNonCandidateRank()
    {
        var json = "{\"body\":\"" + Body + "\",\"ranks\":[{\"name\":\"Grounder\",\"title\":\"Idiot\"}]}";
        var result = EmpireBodyConsolidator.Parse(json, EmpireSeed.Body, new[] { "Curlyquote" }, O);
        Assert.NotNull(result);
        Assert.Empty(result!.RankOps); // Grounder is not a present participant
    }

    [Fact]
    public void Parse_CapsRankOps()
    {
        var json = "{\"body\":\"" + Body + "\",\"ranks\":[" +
            "{\"name\":\"a\",\"title\":\"t1\"},{\"name\":\"b\",\"title\":\"t2\"},{\"name\":\"c\",\"title\":\"t3\"}]}";
        var result = EmpireBodyConsolidator.Parse(json, EmpireSeed.Body, new[] { "a", "b", "c" }, O);
        Assert.NotNull(result);
        Assert.Equal(O.MaxRankOpsPerTick, result!.RankOps.Count);
    }

    [Fact]
    public void Parse_ToleratesFences()
    {
        var json = "```json\n{\"body\":\"" + Body + "\",\"ranks\":[]}\n```";
        var result = EmpireBodyConsolidator.Parse(json, EmpireSeed.Body, System.Array.Empty<string>(), O);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"ranks\":[]}")]           // no body
    [InlineData("{\"body\":\"too short, no headers\"}")]
    public void Parse_ReturnsNullOnBadInput(string text)
        => Assert.Null(EmpireBodyConsolidator.Parse(text, EmpireSeed.Body, System.Array.Empty<string>(), O));
}

public class EmpireStateStoreTests
{
    private static EmpireStateStore NewStore(string path, bool enabled = true) =>
        new(Options.Create(new EmpireStateOptions { Path = path, Enabled = enabled }), NullLogger<EmpireStateStore>.Instance);

    [Fact]
    public void Seeds_WhenNoFile()
    {
        var store = NewStore(Path.Combine(Path.GetTempPath(), "empire_none_" + Guid.NewGuid() + ".json"));
        Assert.Equal(1, store.Current.Version);
        Assert.Contains("Operation Eggshell Dawn", store.Current.Body);
    }

    [Fact]
    public void BuildDirective_IncludesMoodAndBody_AndRankLineWhenPresent()
    {
        var path = Path.Combine(Path.GetTempPath(), "empire_dir_" + Guid.NewGuid() + ".json");
        try
        {
            var store = NewStore(path);
            store.Commit(store.Current with { Ranks = new[] { new Rank("Curlyquote", "Junior Egg-Polisher", 0) } });

            var withRank = store.BuildDirective("Curlyquote");
            Assert.Contains("Mood:", withRank);
            Assert.Contains("Operation Eggshell Dawn", withRank);
            Assert.Contains("Junior Egg-Polisher", withRank);

            var withoutRank = store.BuildDirective("Nobody");
            Assert.DoesNotContain("Junior Egg-Polisher", withoutRank);

            var ambientRankOnly = store.BuildDirective("Curlyquote", includeBody: false);
            Assert.Contains("Junior Egg-Polisher", ambientRankOnly);
            Assert.DoesNotContain("Mood:", ambientRankOnly);
            Assert.DoesNotContain("Operation Eggshell Dawn", ambientRankOnly);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Commit_PersistsAcrossReload()
    {
        var path = Path.Combine(Path.GetTempPath(), "empire_rt_" + Guid.NewGuid() + ".json");
        try
        {
            var a = NewStore(path);
            var newBody = "## The situation now\nNew scheme.\n\n## Lately\n- Old scheme collapsed.";
            a.Commit(a.Current with { Body = newBody });
            var reloaded = NewStore(path);
            Assert.Equal(newBody, reloaded.Current.Body);
            Assert.True(reloaded.Current.Version >= 2);
        }
        finally { File.Delete(path); }
    }
}

public class RecentParticipantsTests
{
    [Fact]
    public void Names_DistinctMostRecentFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var rp = new RecentParticipants(TimeSpan.FromHours(6), () => now);
        rp.Record(1, "Alice");
        now = now.AddMinutes(1);
        rp.Record(2, "Bob");
        now = now.AddMinutes(1);
        rp.Record(1, "Alice"); // Alice again, more recent
        var names = rp.Names(10);
        Assert.Equal(new[] { "Alice", "Bob" }, names);
    }

    [Fact]
    public void Names_ExpiresOldEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var rp = new RecentParticipants(TimeSpan.FromHours(1), () => now);
        rp.Record(1, "Alice");
        now = now.AddHours(2);
        Assert.Empty(rp.Names(10));
    }

    [Fact]
    public void AnyActivitySince_TracksLatest()
    {
        var now = DateTimeOffset.UtcNow;
        var rp = new RecentParticipants(TimeSpan.FromHours(6), () => now);
        var mark = now;
        Assert.False(rp.AnyActivitySince(mark));
        now = now.AddMinutes(5);
        rp.Record(1, "Alice");
        Assert.True(rp.AnyActivitySince(mark));
    }
}

public class EmpireAppraisalTests
{
    [Fact]
    public void SignalConstants_HaveExpectedSign()
    {
        Assert.True(EmpireAppraisal.LaughAtHim.Valence > 0);
        Assert.True(EmpireAppraisal.Panned.Valence < 0);
        Assert.True(EmpireAppraisal.ScamFoiled.Valence > 0);
    }
}

public class EmpireMoodDeltaStoreTests
{
    private static EmpireStateStore NewStore(string path, bool enabled) =>
        new(Options.Create(new EmpireStateOptions { Path = path, Enabled = enabled }), NullLogger<EmpireStateStore>.Instance);

    [Fact]
    public void ApplyMoodDelta_NudgesMood_WhenEnabled()
    {
        var path = Path.Combine(Path.GetTempPath(), "empire_mood_" + Guid.NewGuid() + ".json");
        var store = NewStore(path, enabled: true);
        var before = store.Current.Mood.Valence;
        store.ApplyMoodDelta(new MoodDelta(-0.5, 0.0));
        Assert.True(store.Current.Mood.Valence < before);
    }

    [Fact]
    public void ApplyMoodDelta_NoOp_WhenDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), "empire_mood_off_" + Guid.NewGuid() + ".json");
        var store = NewStore(path, enabled: false);
        var before = store.Current.Mood.Valence;
        store.ApplyMoodDelta(new MoodDelta(-0.5, 0.0));
        Assert.Equal(before, store.Current.Mood.Valence);
    }
}

public class EmpireFlavorTests
{
    private sealed class FixedRng : IRandomProvider
    {
        private readonly double _v;
        public FixedRng(double v) => _v = v;
        public double NextDouble() => _v;
    }

    [Fact]
    public void RollTurnFlavor_Seething_FavorsRantAndAddsMoodCue()
    {
        // Non-ambient baseline cuts are 0.25/0.80; a roll of 0.6 is "medium" at baseline but "rant" when seething.
        var baseline = RobotnikPersona.RollTurnFlavor(new FixedRng(0.6), CreativeInvocationKind.Command, "scheming");
        var seething = RobotnikPersona.RollTurnFlavor(new FixedRng(0.6), CreativeInvocationKind.Command, "seething");
        Assert.NotEqual(baseline.LengthDirective, seething.LengthDirective);
        Assert.Contains("rant", seething.LengthDirective);
        Assert.Contains("seething", seething.MoodDirective);
        Assert.Equal(string.Empty, baseline.MoodDirective); // scheming (baseline) adds no bias
    }

    [Fact]
    public void RollTurnFlavor_NullMood_NoBias()
    {
        var f = RobotnikPersona.RollTurnFlavor(new FixedRng(0.6), CreativeInvocationKind.Command, null);
        Assert.Equal(string.Empty, f.MoodDirective);
    }
}
