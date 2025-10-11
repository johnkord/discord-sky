using Discord;
using Discord.WebSocket;
using DiscordSky.Bot.Bot;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<ChaosSettings>(builder.Configuration.GetSection("Chaos"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<ChaosSettings>>().Value);

builder.Services.AddSingleton(_ => new DiscordSocketConfig
{
	GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent | GatewayIntents.DirectMessages,
	LogLevel = LogSeverity.Info,
	AlwaysDownloadUsers = false,
	MessageCacheSize = 100
});

builder.Services.AddSingleton(sp => new DiscordSocketClient(sp.GetRequiredService<DiscordSocketConfig>()));
builder.Services.AddSingleton<BitStarterService>();
builder.Services.AddSingleton<GremlinStudioService>();
builder.Services.AddSingleton<HeckleCycleService>();
builder.Services.AddSingleton<MischiefQuestService>();
builder.Services.AddHostedService<DiscordBotService>();

var app = builder.Build();
await app.RunAsync();
