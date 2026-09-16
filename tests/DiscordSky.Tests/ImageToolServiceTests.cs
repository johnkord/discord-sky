using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class ImageToolServiceTests
{
    private sealed class StubGenerator : IImageGenerator
    {
        public bool Enabled = true;
        public string? CapturedPrompt;
        public string? CapturedModel;
        public ImageRenderRequest? CapturedRequest;
        public ImageResult Next = ImageResult.Ok(new byte[] { 1, 2, 3 }, "jpg", null);

        public bool IsEnabled => Enabled;

        public Task<ImageResult> RenderAsync(ImageRenderRequest request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return GenerateAsync(request.Prompt, request.Options, cancellationToken);
        }

        public Task<ImageResult> GenerateAsync(string prompt, ImageRequestOptions options, CancellationToken cancellationToken)
        {
            CapturedPrompt = prompt;
            CapturedModel = options.Model;
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeLog : IImageGenerationLog
    {
        public int DayCount;
        public readonly List<ImageGenerationRecord> Records = new();
        public void Record(ImageGenerationRecord record) => Records.Add(record);
        public int CountSuccessesOnUtcDay(DateOnly utcDay) => DayCount;
        public double SumSuccessCostInUtcMonth(DateTimeOffset now) => 0.0;
    }

    private static ImageToolService Build(StubGenerator gen, IImageGenerationLog log, ImageOptions? options = null, IImageReferenceResolver? references = null)
    {
        var opts = options ?? new ImageOptions { PerUserPerHour = 0, GlobalPerDay = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 };
        var budget = new ImageBudget(Options.Create(opts), log);
        return new ImageToolService(budget, gen, log, Options.Create(opts), NullLogger<ImageToolService>.Instance, references);
    }

    [Fact]
    public async Task ReferenceEdit_UsesSunburstPixelsAndReportedCost()
    {
        var gen = new StubGenerator { Next = ImageResult.Ok([1, 2, 3], "png", null) with
            { CostUsd = 0.1234, CostBasis = "token_usage", Usage = new ImageUsage(100, 200, 300), RequestId = "req-test" } };
        var log = new FakeLog();
        var source = new ImageReference([1, 2], "source.png", "image/png", 50);
        var resolver = new StubReferences(new ImageReferenceSet([source], source with { FileName = "mask.png" }));
        var service = Build(gen, log, new ImageOptions { AllowHighQuality = true }, resolver);
        var outcome = await service.GenerateAsync(1, "channel", "make the sky green", ImageTier.Commissioned, CancellationToken.None,
            new ImageGenerationContext("image_command", "command", 51, GuildId: 42, ChannelId: 43),
            new ImageRenderSettings(Quality: "xhigh", Background: "transparent", Action: "edit"));

        Assert.True(outcome.Generated);
        Assert.Equal("gpt-image-2.5-sunburst", gen.CapturedModel);
        Assert.Same(source, Assert.Single(gen.CapturedRequest!.References!));
        Assert.NotNull(gen.CapturedRequest.Mask);
        Assert.Equal("png", gen.CapturedRequest.Options.OutputFormat);
        Assert.Equal("xhigh", gen.CapturedRequest.Options.Quality);
        var record = Assert.Single(log.Records);
        Assert.Equal(0.1234, record.EstCostUsd, 6);
        Assert.Equal("edit", record.Action);
        Assert.Equal(new ulong[] { 50 }, record.ReferenceMessageIds);
        Assert.True(record.HasMask);
        Assert.Equal("req-test", record.RequestId);
        Assert.Equal((ulong)43, resolver.Context!.ChannelId);
    }

    [Fact]
    public async Task ExplicitFreshImage_DoesNotLoadReferences()
    {
        var gen = new StubGenerator();
        var resolver = new StubReferences(new ImageReferenceSet([new ImageReference([1], "reference.png", "image/png")]));
        var service = Build(gen, new FakeLog(), references: resolver);
        await service.GenerateAsync(1, "channel", "a tower", ImageTier.Commissioned, CancellationToken.None,
            settings: new ImageRenderSettings(Action: "generate"));
        Assert.Null(resolver.Context);
        Assert.Null(gen.CapturedRequest!.References);
    }

    [Fact]
    public async Task MissingEditReference_RefusesBeforeProviderCall()
    {
        var gen = new StubGenerator();
        var outcome = await Build(gen, new FakeLog()).GenerateAsync(1, "channel", "change the sky", ImageTier.Commissioned,
            CancellationToken.None, settings: new ImageRenderSettings(Action: "edit"));
        Assert.False(outcome.Generated);
        Assert.Null(gen.CapturedRequest);
        Assert.Contains("requires", outcome.RefusalText);
    }

    [Fact]
    public async Task ChargedFailure_PreservesSpendInDurableLog()
    {
        var gen = new StubGenerator { Next = ImageResult.Fail(ImageResult.ErrorEmpty) with
            { CostUsd = 0.25, CostBasis = "reservation_estimate", PreviewCount = 1 } };
        var log = new FakeLog();
        var outcome = await Build(gen, log).GenerateAsync(1, "channel", "a tower", ImageTier.Commissioned, CancellationToken.None);
        Assert.False(outcome.Generated);
        var record = Assert.Single(log.Records);
        Assert.Equal(0.25, record.EstCostUsd);
        Assert.Equal(1, record.PreviewCount);
    }

    private sealed class StubReferences(ImageReferenceSet references) : IImageReferenceResolver
    {
        public ImageGenerationContext? Context { get; private set; }
        public Task<ImageReferenceSet> ResolveAsync(ImageGenerationContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult(references);
        }
    }

    [Fact]
    public async Task GenerateAsync_AppendsStyleSuffixToPrompt()
    {
        var gen = new StubGenerator();
        var service = Build(gen, new FakeLog());

        await service.GenerateAsync(123, "general", "a statue of my own glorious face", ImageTier.Commissioned, CancellationToken.None);

        Assert.NotNull(gen.CapturedPrompt);
        Assert.StartsWith("a statue of my own glorious face", gen.CapturedPrompt);
        Assert.EndsWith(ImageToolService.StyleSuffix, gen.CapturedPrompt);
    }

    [Fact]
    public async Task GenerateAsync_Success_ReturnsBytesAndFileNameAndLogsOk()
    {
        var gen = new StubGenerator { Next = ImageResult.Ok(new byte[] { 1, 2, 3, 4 }, "jpg", "revised") };
        var log = new FakeLog();
        var service = Build(gen, log);

        var context = new ImageGenerationContext(
            "creative_orchestrator", "ambient", 123, "opp-1", ToolOffered: true, ToolSelected: true, VisualWorth: 0.9,
            EvidenceMessageIds: new ulong[] { 100, 123 }, PromptDigest: "digest-1");
        var outcome = await service.GenerateAsync(
            1, "chan", "draw me a throne", ImageTier.Spontaneous, CancellationToken.None, context);

        Assert.True(outcome.Generated);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, outcome.Bytes);
        Assert.Equal("robotnik.jpg", outcome.FileName);
        Assert.Null(outcome.RefusalText);
        var record = Assert.Single(log.Records);
        Assert.Equal(ImageGenerationRecord.OutcomeOk, record.Outcome);
        Assert.Equal("creative_orchestrator", record.Source);
        Assert.Equal("ambient", record.InvocationKind);
        Assert.Equal("spontaneous", record.Tier);
        Assert.Equal((ulong)123, record.TriggerMessageId);
        Assert.Equal("opp-1", record.OpportunityId);
        Assert.True(record.ToolOffered);
        Assert.True(record.ToolSelected);
        Assert.Equal(0.9, record.VisualWorth);
        Assert.Equal(new ulong[] { 100, 123 }, record.EvidenceMessageIds);
        Assert.Equal("digest-1", record.PromptDigest);
        Assert.Equal(gen.CapturedPrompt, record.FinalPrompt);
    }

    [Fact]
    public async Task GenerateAsync_ModerationBlocked_RefusesAndLogs()
    {
        var gen = new StubGenerator { Next = ImageResult.Fail(ImageResult.ErrorModerationBlocked) };
        var log = new FakeLog();
        var service = Build(gen, log);

        var outcome = await service.GenerateAsync(1, "chan", "draw something", ImageTier.Commissioned, CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.Null(outcome.Bytes);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RefusalText));
        Assert.Contains(log.Records, r => r.Outcome == ImageGenerationRecord.OutcomeModerationBlocked);
    }

    [Fact]
    public async Task GenerateAsync_GenericError_RefusesAndLogsError()
    {
        var gen = new StubGenerator { Next = ImageResult.Fail(ImageResult.ErrorServer) };
        var log = new FakeLog();
        var service = Build(gen, log);

        var outcome = await service.GenerateAsync(1, "chan", "draw", ImageTier.Commissioned, CancellationToken.None);

        Assert.False(outcome.Generated);
        var record = Assert.Single(log.Records);
        Assert.Equal(ImageGenerationRecord.OutcomeError, record.Outcome);
        Assert.Equal(gen.CapturedPrompt, record.FinalPrompt);
    }

    [Fact]
    public void BoundPrompt_NormalizesAndTruncatesPrivateLogContent()
    {
        var prompt = string.Concat("subject\n", new string('x', ImageToolService.MaxLoggedPromptChars + 50));

        var bounded = ImageToolService.BoundPrompt(prompt);

        Assert.NotNull(bounded);
        Assert.Equal(ImageToolService.MaxLoggedPromptChars, bounded.Length);
        Assert.DoesNotContain('\n', bounded);
    }

    [Fact]
    public async Task GenerateAsync_BudgetDenied_RefusesWithoutCallingGeneratorAndLogsReason()
    {
        var gen = new StubGenerator();
        var log = new FakeLog { DayCount = 5 };
        var opts = new ImageOptions { GlobalPerDay = 5, PerUserPerHour = 0, MonthlyUsdGuard = 0, MaxConcurrent = 4 };
        var service = Build(gen, log, opts);

        var outcome = await service.GenerateAsync(1, "chan", "draw", ImageTier.Commissioned, CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RefusalText));
        Assert.Null(gen.CapturedPrompt); // generator must not be called when the budget denies
        var record = Assert.Single(log.Records);
        Assert.Equal(ImageGenerationRecord.OutcomeRefused, record.Outcome);
        Assert.Equal("daily_limit", record.Reason);
    }

    [Fact]
    public async Task GenerateAsync_EmptyPrompt_RefusesAndLogsReason()
    {
        var gen = new StubGenerator();
        var log = new FakeLog();
        var service = Build(gen, log);

        var outcome = await service.GenerateAsync(1, "chan", "   ", ImageTier.Commissioned, CancellationToken.None);

        Assert.False(outcome.Generated);
        Assert.Null(gen.CapturedPrompt);
        var record = Assert.Single(log.Records);
        Assert.Equal(ImageGenerationRecord.OutcomeRefused, record.Outcome);
        Assert.Equal("empty_prompt", record.Reason);
    }

    [Fact]
    public void IsEnabled_ReflectsTheGenerator()
    {
        Assert.True(Build(new StubGenerator { Enabled = true }, new FakeLog()).IsEnabled);
        Assert.False(Build(new StubGenerator { Enabled = false }, new FakeLog()).IsEnabled);
    }

    [Fact]
    public async Task GenerateAsync_TierNeverDowngradesModel()
    {
        var gen = new StubGenerator();
        var opts = new ImageOptions
        {
            Model = "gpt-image-2",
            Quality = "medium",
            PerUserPerHour = 0,
            GlobalPerDay = 0,
            MonthlyUsdGuard = 0,
            MaxConcurrent = 4,
        };
        var service = Build(gen, new FakeLog(), opts);

        await service.GenerateAsync(1, "c", "draw", ImageTier.Spontaneous, CancellationToken.None);
        Assert.Equal("gpt-image-2", gen.CapturedModel);

        await service.GenerateAsync(1, "c", "draw", ImageTier.Commissioned, CancellationToken.None);
        Assert.Equal("gpt-image-2", gen.CapturedModel);
    }

    [Theory]
    [InlineData(true, ImageGenerationRecord.OutcomeNotSelected)]
    [InlineData(false, ImageGenerationRecord.OutcomeNotOffered)]
    public void RecordOpportunity_RecordsTerminalDecision(bool offered, string expectedOutcome)
    {
        var log = new FakeLog();
        var service = Build(new StubGenerator(), log);
        var context = new ImageGenerationContext(
            "creative_orchestrator", "ambient", 123, "opp-2", ToolOffered: offered, ToolSelected: false);

        service.RecordOpportunity(1, "chan", ImageTier.Spontaneous, context);

        var record = Assert.Single(log.Records);
        Assert.Equal(expectedOutcome, record.Outcome);
        Assert.Equal("opp-2", record.OpportunityId);
        Assert.Equal(offered, record.ToolOffered);
        Assert.False(record.ToolSelected);
    }
}
