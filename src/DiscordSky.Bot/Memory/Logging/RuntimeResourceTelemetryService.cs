using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordSky.Bot.Memory.Logging;

public sealed class RuntimeResourceTelemetryService : BackgroundService
{
    private readonly IRecallTelemetrySink _telemetry;
    private readonly ILogger<RuntimeResourceTelemetryService> _logger;
    private readonly IRuntimeMemoryReader _memoryReader;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sampleInterval;
    private string? _lastBand;

    public RuntimeResourceTelemetryService(
        IRecallTelemetrySink telemetry,
        IOptions<TelemetryOptions> options,
        ILogger<RuntimeResourceTelemetryService> logger)
        : this(telemetry, options, logger, new CgroupRuntimeMemoryReader(), TimeProvider.System)
    {
    }

    internal RuntimeResourceTelemetryService(
        IRecallTelemetrySink telemetry,
        IOptions<TelemetryOptions> options,
        ILogger<RuntimeResourceTelemetryService> logger,
        IRuntimeMemoryReader memoryReader,
        TimeProvider timeProvider)
    {
        _telemetry = telemetry;
        _logger = logger;
        _memoryReader = memoryReader;
        _timeProvider = timeProvider;
        _sampleInterval = options.Value.ResourceSampleInterval > TimeSpan.Zero
            ? options.Value.ResourceSampleInterval
            : TimeSpan.FromMinutes(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RecordSample();
        using var timer = new PeriodicTimer(_sampleInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            RecordSample();
        }
    }

    internal void RecordSample()
    {
        RuntimeMemorySample sample;
        try
        {
            sample = _memoryReader.Read();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Runtime memory sample failed.");
            return;
        }

        var utilization = sample.LimitBytes is > 0
            ? Math.Clamp((double)sample.CurrentBytes / sample.LimitBytes.Value, 0, 1)
            : (double?)null;
        var band = Classify(utilization);
        var now = _timeProvider.GetUtcNow();
        _telemetry.Emit(CreateEvent(now, "sample", band, null, sample, utilization));

        if (string.Equals(_lastBand, band, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _lastBand;
        _lastBand = band;
        _telemetry.Emit(CreateEvent(now, "transition", band, previous, sample, utilization));
        if (band is "warning" or "critical")
        {
            _logger.LogWarning(
                "Runtime memory entered {Band} band: {CurrentBytes}/{LimitBytes} bytes ({Utilization:P1}).",
                band,
                sample.CurrentBytes,
                sample.LimitBytes,
                utilization);
        }
        else if (previous is "warning" or "critical")
        {
            _logger.LogInformation("Runtime memory recovered to {Band} band.", band);
        }
    }

    internal static string Classify(double? utilization) => utilization switch
    {
        null => "limit_unavailable",
        >= 0.90 => "critical",
        >= 0.80 => "warning",
        _ => "normal",
    };

    private static TelemetryEvent CreateEvent(
        DateTimeOffset timestamp,
        string stage,
        string band,
        string? previous,
        RuntimeMemorySample sample,
        double? utilization) => new(
            Timestamp: timestamp,
            EventType: TelemetryEventTypes.RuntimeResource,
            Outcome: band,
            BaselineOutcome: previous,
            Stage: stage,
            MemoryCurrentBytes: sample.CurrentBytes,
            MemoryLimitBytes: sample.LimitBytes,
            Utilization: utilization);
}

internal interface IRuntimeMemoryReader
{
    RuntimeMemorySample Read();
}

internal sealed record RuntimeMemorySample(long CurrentBytes, long? LimitBytes);

internal sealed class CgroupRuntimeMemoryReader : IRuntimeMemoryReader
{
    private static readonly (string Current, string Limit)[] Paths =
    [
        ("/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory.max"),
        ("/sys/fs/cgroup/memory/memory.usage_in_bytes", "/sys/fs/cgroup/memory/memory.limit_in_bytes"),
    ];

    public RuntimeMemorySample Read()
    {
        foreach (var paths in Paths)
        {
            if (!TryReadLong(paths.Current, out var current))
            {
                continue;
            }

            return new RuntimeMemorySample(
                current,
                TryReadLong(paths.Limit, out var limit) && limit > 0 ? limit : null);
        }

        return new RuntimeMemorySample(Process.GetCurrentProcess().WorkingSet64, null);
    }

    private static bool TryReadLong(string path, out long value)
    {
        value = 0;
        try
        {
            var text = File.ReadAllText(path).Trim();
            return !string.Equals(text, "max", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}