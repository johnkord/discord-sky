using System.Diagnostics;
using System.Globalization;
using DiscordSky.Bot.Memory.Reception;
using DiscordSky.Bot.Orchestration;
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
    private readonly Func<RuntimeApplicationResourceSample>? _applicationSample;
    private string? _lastBand;

    public RuntimeResourceTelemetryService(
        IRecallTelemetrySink telemetry,
        IOptions<TelemetryOptions> options,
        ILogger<RuntimeResourceTelemetryService> logger,
        IUserMemoryStore userMemoryStore,
        MediaSemanticCache mediaSemanticCache,
        SentMessageRegistry sentMessages)
        : this(
            telemetry,
            options,
            logger,
            new CgroupRuntimeMemoryReader(),
            TimeProvider.System,
            () => new RuntimeApplicationResourceSample(
                userMemoryStore is FileBackedUserMemoryStore fileStore ? fileStore.CachedUserCount : null,
                mediaSemanticCache.EntryCount,
                sentMessages.Count))
    {
    }

    internal RuntimeResourceTelemetryService(
        IRecallTelemetrySink telemetry,
        IOptions<TelemetryOptions> options,
        ILogger<RuntimeResourceTelemetryService> logger,
        IRuntimeMemoryReader memoryReader,
        TimeProvider timeProvider,
        Func<RuntimeApplicationResourceSample>? applicationSample = null)
    {
        _telemetry = telemetry;
        _logger = logger;
        _memoryReader = memoryReader;
        _timeProvider = timeProvider;
        _applicationSample = applicationSample;
        var configuredInterval = options.Value.ResourceSampleInterval > TimeSpan.Zero
            ? options.Value.ResourceSampleInterval
            : TimeSpan.FromMinutes(5);
        _sampleInterval = configuredInterval < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : configuredInterval;
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
        RuntimeApplicationResourceSample? application = null;
        try
        {
            application = _applicationSample?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Runtime application resource sample failed.");
        }
        _telemetry.Emit(CreateEvent(now, "sample", band, null, sample, application, utilization));

        if (string.Equals(_lastBand, band, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _lastBand;
        _lastBand = band;
        _telemetry.Emit(CreateEvent(now, "transition", band, previous, sample, application, utilization));
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

    internal TimeSpan SampleInterval => _sampleInterval;

    private static TelemetryEvent CreateEvent(
        DateTimeOffset timestamp,
        string stage,
        string band,
        string? previous,
        RuntimeMemorySample sample,
        RuntimeApplicationResourceSample? application,
        double? utilization) => new(
            Timestamp: timestamp,
            EventType: TelemetryEventTypes.RuntimeResource,
            Outcome: band,
            BaselineOutcome: previous,
            Stage: stage,
            MemoryCurrentBytes: sample.CurrentBytes,
            MemoryLimitBytes: sample.LimitBytes,
            Utilization: utilization,
            ProcessRssBytes: sample.ProcessRssBytes,
            ChildProcessRssBytes: sample.ChildProcessRssBytes,
            ChildProcessCount: sample.ChildProcessCount,
            ManagedHeapBytes: sample.ManagedHeapBytes,
            GcHeapSizeBytes: sample.GcHeapSizeBytes,
            GcFragmentedBytes: sample.GcFragmentedBytes,
            GcGen0Count: sample.GcGen0Count,
            GcGen1Count: sample.GcGen1Count,
            GcGen2Count: sample.GcGen2Count,
            ThreadCount: sample.ThreadCount,
            UserMemoryCacheCount: application?.UserMemoryCacheCount,
            MediaSemanticCacheCount: application?.MediaSemanticCacheCount,
            SentMessageRegistryCount: application?.SentMessageRegistryCount);
}

internal interface IRuntimeMemoryReader
{
    RuntimeMemorySample Read();
}

internal sealed record RuntimeMemorySample(
    long CurrentBytes,
    long? LimitBytes,
    long? ProcessRssBytes = null,
    long? ChildProcessRssBytes = null,
    int? ChildProcessCount = null,
    long? ManagedHeapBytes = null,
    long? GcHeapSizeBytes = null,
    long? GcFragmentedBytes = null,
    int? GcGen0Count = null,
    int? GcGen1Count = null,
    int? GcGen2Count = null,
    int? ThreadCount = null);

internal sealed record RuntimeApplicationResourceSample(
    int? UserMemoryCacheCount,
    int? MediaSemanticCacheCount,
    int? SentMessageRegistryCount);

internal sealed class CgroupRuntimeMemoryReader : IRuntimeMemoryReader
{
    private static readonly (string Current, string Limit)[] Paths =
    [
        ("/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory.max"),
        ("/sys/fs/cgroup/memory/memory.usage_in_bytes", "/sys/fs/cgroup/memory/memory.limit_in_bytes"),
    ];

    public RuntimeMemorySample Read()
    {
        var processMetrics = ReadProcessMetrics();
        foreach (var paths in Paths)
        {
            if (!TryReadLong(paths.Current, out var current))
            {
                continue;
            }

            return new RuntimeMemorySample(
                current,
                TryReadLong(paths.Limit, out var limit) && limit > 0 ? limit : null,
                processMetrics.ProcessRssBytes,
                processMetrics.ChildProcessRssBytes,
                processMetrics.ChildProcessCount,
                processMetrics.ManagedHeapBytes,
                processMetrics.GcHeapSizeBytes,
                processMetrics.GcFragmentedBytes,
                processMetrics.GcGen0Count,
                processMetrics.GcGen1Count,
                processMetrics.GcGen2Count,
                processMetrics.ThreadCount);
        }

        return new RuntimeMemorySample(
            processMetrics.ProcessRssBytes ?? 0,
            null,
            processMetrics.ProcessRssBytes,
            processMetrics.ChildProcessRssBytes,
            processMetrics.ChildProcessCount,
            processMetrics.ManagedHeapBytes,
            processMetrics.GcHeapSizeBytes,
            processMetrics.GcFragmentedBytes,
            processMetrics.GcGen0Count,
            processMetrics.GcGen1Count,
            processMetrics.GcGen2Count,
            processMetrics.ThreadCount);
    }

    private static ProcessRuntimeMetrics ReadProcessMetrics()
    {
        long? processRss = null;
        int? threadCount = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            processRss = process.WorkingSet64;
            threadCount = process.Threads.Count;
        }
        catch (InvalidOperationException)
        {
        }

        long? managedHeap = null;
        long? heapSize = null;
        long? fragmented = null;
        try
        {
            managedHeap = GC.GetTotalMemory(forceFullCollection: false);
            var info = GC.GetGCMemoryInfo();
            heapSize = info.HeapSizeBytes;
            fragmented = info.FragmentedBytes;
        }
        catch (InvalidOperationException)
        {
        }

        var (childRss, childCount) = ReadDirectChildren(Environment.ProcessId);
        return new ProcessRuntimeMetrics(
            processRss,
            childRss,
            childCount,
            managedHeap,
            heapSize,
            fragmented,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            threadCount);
    }

    private static (long? RssBytes, int? Count) ReadDirectChildren(int parentPid)
    {
        try
        {
            long totalRssBytes = 0;
            var count = 0;
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var statusPath = Path.Combine(directory, "status");
                if (!TryReadStatusValue(statusPath, "PPid:", out var ppid) || ppid != parentPid)
                {
                    continue;
                }

                count++;
                if (TryReadStatusValue(statusPath, "VmRSS:", out var rssKiB))
                {
                    totalRssBytes += rssKiB * 1024;
                }
            }
            return (totalRssBytes, count);
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static bool TryReadStatusValue(string path, string key, out long value)
    {
        value = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith(key, StringComparison.Ordinal))
                {
                    continue;
                }

                var token = line[key.Length..].TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return false;
    }

    private sealed record ProcessRuntimeMetrics(
        long? ProcessRssBytes,
        long? ChildProcessRssBytes,
        int? ChildProcessCount,
        long? ManagedHeapBytes,
        long? GcHeapSizeBytes,
        long? GcFragmentedBytes,
        int? GcGen0Count,
        int? GcGen1Count,
        int? GcGen2Count,
        int? ThreadCount);

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