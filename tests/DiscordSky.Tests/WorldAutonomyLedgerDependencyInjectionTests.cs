using DiscordSky.Bot.Orchestration.Autonomy;
using DiscordSky.Bot.Configuration;
using DiscordSky.Bot.Memory.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiscordSky.Tests;

public sealed class WorldAutonomyLedgerDependencyInjectionTests
{
    [Fact]
    public void Ledger_ResolvesFromConfiguredOptions()
    {
        using var directory = new TemporaryDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<WorldAutonomyOptions>>(Options.Create(new WorldAutonomyOptions
        {
            LedgerPath = Path.Combine(directory.Path, "world-autonomy.json")
        }));
        services.AddSingleton<FileBackedWorldAutonomyLedger>();

        using var provider = services.BuildServiceProvider();
        var ledger = provider.GetRequiredService<FileBackedWorldAutonomyLedger>();

        Assert.NotNull(ledger);
    }

    [Fact]
    public void ProviderGuard_ResolvesThroughConfiguredLlmOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<LlmOptions>>(Options.Create(new LlmOptions
        {
            Guard = new LlmProviderGuardOptions
            {
                HourlyUsdLimit = -1,
                DailyUsdLimit = 3,
            },
        }));
        services.AddSingleton<IRecallTelemetrySink, NoOpTelemetrySink>();
        services.AddSingleton<LlmProviderGuard>();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<LlmProviderGuard>());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"discord-sky-autonomy-di-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}