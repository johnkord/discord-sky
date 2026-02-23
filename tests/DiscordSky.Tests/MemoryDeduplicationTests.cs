using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Models.Orchestration;

namespace DiscordSky.Tests;

public class MemoryDeduplicationTests
{
    private static UserMemory MakeMemory(string content) =>
        new(content, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0);

    // --- IsDuplicateMemory ---

    [Fact]
    public void ExactDuplicate_ReturnsTrue()
    {
        var existing = new List<UserMemory> { MakeMemory("Likes cats") };
        Assert.True(DiscordBotService.IsDuplicateMemory("Likes cats", existing));
    }

    [Fact]
    public void CaseInsensitiveDuplicate_ReturnsTrue()
    {
        var existing = new List<UserMemory> { MakeMemory("Likes Cats") };
        Assert.True(DiscordBotService.IsDuplicateMemory("likes cats", existing));
    }

    [Fact]
    public void HighOverlapDuplicate_ReturnsTrue()
    {
        // "likes cats very much" vs "likes cats very much indeed" — high Jaccard overlap
        var existing = new List<UserMemory> { MakeMemory("Likes cats very much") };
        Assert.True(DiscordBotService.IsDuplicateMemory("Likes cats very much indeed", existing));
    }

    [Fact]
    public void SimilarPhrasing_ReturnsTrue()
    {
        var existing = new List<UserMemory> { MakeMemory("User enjoys playing guitar") };
        Assert.True(DiscordBotService.IsDuplicateMemory("Enjoys playing guitar", existing));
    }

    [Fact]
    public void CompletelyDifferent_ReturnsFalse()
    {
        var existing = new List<UserMemory> { MakeMemory("Likes cats") };
        Assert.False(DiscordBotService.IsDuplicateMemory("Works as a software engineer", existing));
    }

    [Fact]
    public void PartialOverlap_BelowThreshold_ReturnsFalse()
    {
        // Small word overlap shouldn't count as duplicate
        var existing = new List<UserMemory> { MakeMemory("Likes cats") };
        Assert.False(DiscordBotService.IsDuplicateMemory("Likes dogs and horses", existing));
    }

    [Fact]
    public void EmptyExistingMemories_ReturnsFalse()
    {
        Assert.False(DiscordBotService.IsDuplicateMemory("Likes cats", new List<UserMemory>()));
    }

    [Fact]
    public void EmptyCandidate_ReturnsFalse()
    {
        var existing = new List<UserMemory> { MakeMemory("Likes cats") };
        Assert.False(DiscordBotService.IsDuplicateMemory("", existing));
    }

    [Fact]
    public void MultipleExistingMemories_MatchesAny()
    {
        var existing = new List<UserMemory>
        {
            MakeMemory("Lives in Canada"),
            MakeMemory("Likes cats"),
            MakeMemory("Works as a software engineer")
        };

        Assert.True(DiscordBotService.IsDuplicateMemory("Likes cats", existing));
        Assert.False(DiscordBotService.IsDuplicateMemory("Enjoys painting landscapes", existing));
    }

    [Fact]
    public void CustomThreshold_Strict_RequiresHigherOverlap()
    {
        var existing = new List<UserMemory> { MakeMemory("Likes cats very much") };

        // At 0.9 threshold, small differences should prevent matching
        Assert.False(DiscordBotService.IsDuplicateMemory("Likes cats very much indeed", existing, threshold: 0.9));

        // Exact match still works
        Assert.True(DiscordBotService.IsDuplicateMemory("Likes cats very much", existing, threshold: 0.9));
    }

    [Fact]
    public void CustomThreshold_Lenient_CatchesMoreDuplicates()
    {
        var existing = new List<UserMemory> { MakeMemory("Enjoys programming in Python") };

        // With 0.5 threshold, broader overlap matches
        Assert.True(DiscordBotService.IsDuplicateMemory("Enjoys programming", existing, threshold: 0.5));
    }

    [Fact]
    public void PunctuationIgnored_StillMatches()
    {
        var existing = new List<UserMemory> { MakeMemory("Likes cats, dogs, and birds!") };
        Assert.True(DiscordBotService.IsDuplicateMemory("likes cats dogs and birds", existing));
    }

    [Fact]
    public void SingleCharWords_Ignored()
    {
        // Single-char words like "I", "a" are stripped — ensuring they don't inflate similarity
        var existing = new List<UserMemory> { MakeMemory("I am a cat lover") };
        Assert.True(DiscordBotService.IsDuplicateMemory("I am a cat lover too", existing));
    }
}
