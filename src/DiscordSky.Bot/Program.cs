using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.Images;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Memory.Logging;
using DiscordSky.Bot.Memory.Scoring;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;
// Microsoft.Extensions.AI also defines an IImageGenerator; bind the bare name to ours (the only one used here).
using IImageGenerator = DiscordSky.Bot.Integrations.Images.IImageGenerator;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://+:8080");

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<ChaosSettings>(builder.Configuration.GetSection("Chaos"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.Configure<MemoryRelevanceOptions>(builder.Configuration.GetSection(MemoryRelevanceOptions.SectionName));
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
builder.Services.Configure<TranscriptOptions>(builder.Configuration.GetSection(TranscriptOptions.SectionName));
builder.Services.Configure<ReactionOptions>(builder.Configuration.GetSection(ReactionOptions.SectionName));
builder.Services.Configure<ImageOptions>(builder.Configuration.GetSection(ImageOptions.SectionName));

builder.Services.AddSingleton(_ => new DiscordSocketConfig
{
	GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent | GatewayIntents.DirectMessages | GatewayIntents.GuildMessageReactions,
	LogLevel = LogSeverity.Info,
	AlwaysDownloadUsers = false,
	MessageCacheSize = 100
});

builder.Services.AddSingleton(sp => new DiscordSocketClient(sp.GetRequiredService<DiscordSocketConfig>()));

// Creates an IChatClient using the active LLM provider from config.
// The provider is selected by LLM:ActiveProvider ("OpenAI", "xAI", etc.).
// Per-request model overrides via ChatOptions.ModelId continue to work.
builder.Services.AddSingleton<IChatClient>(sp =>
{
	var llmOptions = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
	var provider = llmOptions.GetActiveProvider(); // throws if ActiveProvider key not found
	var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("LlmProvider");

	if (string.IsNullOrWhiteSpace(provider.ApiKey))
	{
		logger.LogCritical("LLM provider '{Provider}' has no API key configured. Set LLM:Providers:{Provider}:ApiKey.",
			llmOptions.ActiveProvider, llmOptions.ActiveProvider);
		throw new InvalidOperationException(
			$"LLM provider '{llmOptions.ActiveProvider}' has no API key configured.");
	}

	OpenAIClientOptions? clientOptions = null;
	if (!string.IsNullOrWhiteSpace(provider.Endpoint))
	{
		clientOptions = new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint) };
		logger.LogInformation("LLM provider '{Provider}' configured: endpoint={Endpoint}, model={Model}",
			llmOptions.ActiveProvider, provider.Endpoint, provider.ChatModel);
	}
	else
	{
		logger.LogInformation("LLM provider '{Provider}' configured: endpoint=OpenAI (default), model={Model}",
			llmOptions.ActiveProvider, provider.ChatModel);
	}

	var openAiClient = clientOptions is not null
		? new OpenAIClient(new System.ClientModel.ApiKeyCredential(provider.ApiKey), clientOptions)
		: new OpenAIClient(provider.ApiKey);

	if (provider.UseResponsesApi)
	{
		logger.LogInformation("Using Responses API for provider '{Provider}'", llmOptions.ActiveProvider);
		return openAiClient
			.GetResponsesClient(provider.ChatModel)
			.AsIChatClient();
	}

	return openAiClient
		.GetChatClient(provider.ChatModel)
		.AsIChatClient();
});

builder.Services.AddHttpClient<TweetUnfurler>();
builder.Services.AddHttpClient<RedditUnfurler>();
builder.Services.AddHttpClient<HackerNewsUnfurler>();
builder.Services.AddHttpClient<WikipediaUnfurler>();
builder.Services.AddHttpClient<WebContentUnfurler>();
builder.Services.AddSingleton<ILinkUnfurler>(sp =>
{
	var unfurlers = new ILinkUnfurler[]
	{
		sp.GetRequiredService<TweetUnfurler>(),         // Specialized: tweets
		sp.GetRequiredService<RedditUnfurler>(),        // Specialized: Reddit posts/comments
		sp.GetRequiredService<HackerNewsUnfurler>(),    // Specialized: HN stories/comments
		sp.GetRequiredService<WikipediaUnfurler>(),     // Specialized: Wikipedia articles
		sp.GetRequiredService<WebContentUnfurler>()     // General: everything else
	};
	return new CompositeUnfurler(unfurlers, sp.GetRequiredService<ILogger<CompositeUnfurler>>());
});
builder.Services.AddSingleton<SafetyFilter>();
builder.Services.AddSingleton<ContextAggregator>();
builder.Services.AddSingleton<IMemoryScorer, LexicalMemoryScorer>();
builder.Services.AddSingleton<CreativeOrchestrator>();
builder.Services.AddSingleton<IUserMemoryStore, FileBackedUserMemoryStore>();
// Telemetry sink: singleton (for emit) and hosted (for daily-file lifecycle + 30-day retention sweep).
// See docs/recall_feature_review_2026-05-26.md §7.1.
builder.Services.AddSingleton<FileBackedTelemetrySink>();
builder.Services.AddSingleton<IRecallTelemetrySink>(sp => sp.GetRequiredService<FileBackedTelemetrySink>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileBackedTelemetrySink>());
// Conversation transcript sink: captures full prompt + reply for quality evaluation. Off by default
// (Transcript:Enabled); writes raw content to the PVC when enabled. See docs/improvement_opportunities_2026-06-10.md H2.
builder.Services.AddSingleton<FileBackedTranscriptSink>();
builder.Services.AddSingleton<ITranscriptSink>(sp => sp.GetRequiredService<FileBackedTranscriptSink>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileBackedTranscriptSink>());
// Reaction sink: records reactions on the bot's own messages, the first real reception signal.
// On by default (Reactions:Enabled); low-sensitivity (emoji + IDs). See docs/fun_assessment_2026-06-25.md P1.
builder.Services.AddSingleton<FileBackedReactionSink>();
builder.Services.AddSingleton<IReactionSink>(sp => sp.GetRequiredService<FileBackedReactionSink>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileBackedReactionSink>());
// Image generation (docs/image_generation_design.md). Off by default (Image:Enabled). The durable log
// both records spend and backs the budget's restart-surviving daily cap and monthly guard.
builder.Services.AddSingleton<FileBackedImageGenerationLog>();
builder.Services.AddSingleton<IImageGenerationLog>(sp => sp.GetRequiredService<FileBackedImageGenerationLog>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FileBackedImageGenerationLog>());
builder.Services.AddSingleton<ImageBudget>();
builder.Services.AddSingleton<ImageRewriter>();
// The generator resolves the OpenAI image key independently of the active chat provider (images always
// go through OpenAI). Falls back to a disabled NoOp when off or when no key is present.
builder.Services.AddSingleton<IImageGenerator>(sp =>
{
	var imageOptions = sp.GetRequiredService<IOptions<ImageOptions>>().Value;
	var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
	var logger = loggerFactory.CreateLogger("ImageGenerator");

	if (!imageOptions.Enabled)
	{
		logger.LogInformation("Image generation disabled (Image:Enabled=false).");
		return new NoOpImageGenerator();
	}

	var llmOptions = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
	if (!llmOptions.Providers.TryGetValue(imageOptions.ProviderName, out var provider)
		|| string.IsNullOrWhiteSpace(provider.ApiKey))
	{
		logger.LogWarning(
			"Image generation enabled but provider '{Provider}' has no API key; running disabled. Set LLM:Providers:{Provider}:ApiKey.",
			imageOptions.ProviderName, imageOptions.ProviderName);
		return new NoOpImageGenerator();
	}

	var openAiClient = string.IsNullOrWhiteSpace(provider.Endpoint)
		? new OpenAIClient(provider.ApiKey)
		: new OpenAIClient(new System.ClientModel.ApiKeyCredential(provider.ApiKey),
			new OpenAIClientOptions { Endpoint = new Uri(provider.Endpoint) });

	logger.LogInformation("Image generation ENABLED (provider={Provider}, model={Model}).",
		imageOptions.ProviderName, imageOptions.Model);
	return new OpenAIImageGenerator(
		openAiClient,
		loggerFactory.CreateLogger<OpenAIImageGenerator>());
});
// Shared generation core used by both the !sky(image) command and the model-decided generate_image tool.
builder.Services.AddSingleton<ImageToolService>();
// LLM auth self-test: surfaces silent 401 incidents as pod crashes instead of healthy-but-broken state.
// See docs/recall_feature_review_2026-05-26.md §7.2.
builder.Services.AddHttpClient();
builder.Services.AddHostedService<LlmAuthCheckHostedService>();
builder.Services.AddHostedService<DiscordBotService>();

var app = builder.Build();

app.MapGet("/healthz", (DiscordSocketClient client) =>
{
	var isConnected = client.ConnectionState == ConnectionState.Connected;
	return isConnected
		? Results.Ok(new { status = "healthy", connection = "connected" })
		: Results.Json(new { status = "degraded", connection = client.ConnectionState.ToString() }, statusCode: 503);
});

await app.RunAsync();
