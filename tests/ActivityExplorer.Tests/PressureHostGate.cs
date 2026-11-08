using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ActivityExplorer.Tests;

internal sealed class PressureHostGate : IDisposable
{
    private const uint FormatDouble = 0x200;
    private readonly Settings settings;
    private readonly nint query;
    private readonly nint currentDiskQueue;
    private readonly nint averageDiskQueue;
    private readonly nint cpu;

    public PressureHostGate(Settings settings, string volume)
    {
        if (settings.PollMs < 250 || settings.ConsecutiveSamples < 2 || settings.MaxWaitMs <= settings.PollMs * settings.ConsecutiveSamples ||
            settings.MaxCurrentDiskQueue < 0 || settings.MaxAverageDiskQueue < 0 || settings.MaxCpuPercent is < 0 or > 100)
            throw new ArgumentException("Invalid pressure host gate policy.", nameof(settings));
        this.settings = settings;
        Check(PdhOpenQueryW(0, 0, out query));
        try
        {
            Check(PdhAddEnglishCounterW(query, $@"\LogicalDisk({volume})\Current Disk Queue Length", 0, out currentDiskQueue));
            Check(PdhAddEnglishCounterW(query, $@"\LogicalDisk({volume})\Avg. Disk Queue Length", 0, out averageDiskQueue));
            Check(PdhAddEnglishCounterW(query, @"\Processor(_Total)\% Processor Time", 0, out cpu));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async Task<WaitResult> WaitAsync(string scenario, string regime)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var start = Stopwatch.GetTimestamp();
        var observations = new List<Observation>();
        var consecutive = 0;
        var seeded = PdhCollectQueryData(query);
        if (!IsTransient(seeded)) Check(seeded);
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < settings.MaxWaitMs)
        {
            await Task.Delay(settings.PollMs);
            var observation = Collect();
            observations.Add(observation);
            var quiet = observation.ErrorCode is null && observation.CurrentDiskQueue <= settings.MaxCurrentDiskQueue &&
                observation.AverageDiskQueue <= settings.MaxAverageDiskQueue && observation.CpuPercent <= settings.MaxCpuPercent;
            consecutive = quiet ? consecutive + 1 : 0;
            if (consecutive == settings.ConsecutiveSamples &&
                Stopwatch.GetElapsedTime(start).TotalMilliseconds <= settings.MaxWaitMs)
                return new(scenario, regime, startedUtc, DateTimeOffset.UtcNow, true, observations);
        }
        return new(scenario, regime, startedUtc, DateTimeOffset.UtcNow, false, observations);
    }

    public void Dispose() => _ = PdhCloseQuery(query);

    private Observation Collect()
    {
        var status = PdhCollectQueryData(query);
        if (IsTransient(status)) return Unavailable(status);
        Check(status);
        if (!TryRead(currentDiskQueue, out var current, out status) ||
            !TryRead(averageDiskQueue, out var average, out status) ||
            !TryRead(cpu, out var cpuPercent, out status))
            return Unavailable(status);
        return new(DateTimeOffset.UtcNow, current, average, cpuPercent, null);
    }

    private static Observation Unavailable(uint status) =>
        new(DateTimeOffset.UtcNow, null, null, null, $"0x{status:X8}");

    private static bool TryRead(nint counter, out double number, out uint status)
    {
        status = PdhGetFormattedCounterValue(counter, FormatDouble, 0, out var value);
        if (status == 0 && value.Status > 1) status = value.Status;
        if (status == 0 && (!double.IsFinite(value.Number) || value.Number < 0)) status = 0xC0000BBA;
        number = value.Number;
        if (IsTransient(status)) return false;
        Check(status);
        return true;
    }

    // PDH can report a transient missing sample or reset its interval counter between polls.
    private static bool IsTransient(uint status) => status is 0x800007D4 or 0x800007D5 or 0x800007D6 or 0x800007D7 or 0x800007D8 or 0xC0000BBA;

    private static void Check(uint status)
    {
        if (status != 0) throw new InvalidOperationException($"The host performance counter call failed (0x{status:X8}).");
    }

    internal sealed record Settings(int PollMs, int ConsecutiveSamples, double MaxCurrentDiskQueue,
        double MaxAverageDiskQueue, double MaxCpuPercent, int MaxWaitMs);
    internal sealed record Observation(DateTimeOffset Utc, double? CurrentDiskQueue, double? AverageDiskQueue,
        double? CpuPercent, string? ErrorCode);
    internal sealed record WaitResult(string Scenario, string Regime, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
        bool Passed, IReadOnlyList<Observation> Observations);

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Number;
    }

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(nint dataSource, nuint userData, out nint query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, nint counterType, out CounterValue value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
