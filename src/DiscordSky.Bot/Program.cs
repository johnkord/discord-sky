using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.LinkUnfurling;
using DiscordSky.Bot.Memory;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://+:8080");

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<ChaosSettings>(builder.Configuration.GetSection("Chaos"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));

builder.Services.AddSingleton(_ => new DiscordSocketConfig
{
	GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent | GatewayIntents.DirectMessages,
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
builder.Services.AddSingleton<CreativeOrchestrator>();
builder.Services.AddSingleton<IUserMemoryStore, FileBackedUserMemoryStore>();
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
