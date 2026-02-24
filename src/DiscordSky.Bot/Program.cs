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
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));

builder.Services.AddSingleton(_ => new DiscordSocketConfig
{
	GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent | GatewayIntents.DirectMessages,
	LogLevel = LogSeverity.Info,
	AlwaysDownloadUsers = false,
	MessageCacheSize = 100
});

builder.Services.AddSingleton(sp => new DiscordSocketClient(sp.GetRequiredService<DiscordSocketConfig>()));

// Note: The ChatClient is created with a default model, but CreativeOrchestrator can override
// the model per-request via ChatOptions.ModelId (e.g. for per-persona model routing).
// The M.E.AI OpenAI adapter correctly applies the per-request ModelId when set.
builder.Services.AddSingleton<IChatClient>(sp =>
{
	var options = sp.GetRequiredService<IOptions<OpenAIOptions>>().Value;
	return new OpenAIClient(options.ApiKey)
		.GetChatClient(options.ChatModel)
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
