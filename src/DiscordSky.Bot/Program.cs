using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Integrations.OpenAI;
using DiscordSky.Bot.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<ChaosSettings>(builder.Configuration.GetSection("Chaos"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ChaosSettings>>().Value);

builder.Services.AddSingleton(_ => new DiscordSocketConfig
{
	GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent | GatewayIntents.DirectMessages,
	LogLevel = LogSeverity.Info,
	AlwaysDownloadUsers = false,
	MessageCacheSize = 100
});

builder.Services.AddSingleton(sp => new DiscordSocketClient(sp.GetRequiredService<DiscordSocketConfig>()));
builder.Services.AddHttpClient<IOpenAiClient, OpenAiClient>();
builder.Services.AddSingleton<SafetyFilter>();
builder.Services.AddSingleton<ContextAggregator>();
builder.Services.AddSingleton<CreativeOrchestrator>();
builder.Services.AddHostedService<DiscordBotService>();

var app = builder.Build();
await app.RunAsync();
