using System.Diagnostics;
using System.Text.Json;
using LiveChartsCore.Defaults;

namespace Benchmarks;

// Source-only measurements: these do not measure chart rendering or UI frame rates.
internal static class IndexBenchmark
{
    public static int Run(ReadOnlySpan<string> args)
    {
        var count = args.Length > 0 ? int.Parse(args[0]) : 1_000_000;
        var iterations = args.Length > 1 ? int.Parse(args[1]) : 300;
        var output = args.Length > 2 ? args[2] : "artifacts/index.json";
        if (count < 100 || iterations < 10) throw new ArgumentOutOfRangeException(nameof(args));
        const int pixels = 1000;
        // Warm the mapping/index-building JIT before the timed large source build.
        var warmup = new TimeSeriesBuffer<double>(x => x, Signal);
        for (var i = 0; i < 20_000; i++) warmup.Append(i);
        var selected = new List<int>(pixels * 5 + 2);
        warmup.Select(0, warmup.Count - 1, pixels, selected);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var memoryBefore = GC.GetTotalMemory(true);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var source = new TimeSeriesBuffer<double>(x => x, Signal);
        for (var i = 0; i < count; i++) source.Append(i);
        watch.Stop();
        var buildMs = watch.Elapsed.TotalMilliseconds;
        var buildAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var retainedBytes = GC.GetTotalMemory(true) - memoryBefore;
        var sink = 0d;
        var results = new List<object>
        {
            Measure("select-full-history-1000px", () =>
            {
                source.Select(0, source.Count - 1, pixels, selected);
                sink += selected.Count;
            }, iterations, 1),
            Measure("select-latest-10000-1000px", () =>
            {
                source.Select(Math.Max(0, source.Count - 10000), source.Count - 1, pixels, selected);
                sink += selected.Count;
            }, iterations, 1),
            Measure("exact-visible-y-bounds", () =>
            {
                source.TryGetBounds(count * .213, count * .891, out var min, out var max);
                sink += min + max;
            }, iterations, 100),
            Measure("nearest-original-by-x", () => sink += source.FindNearestIndex(count * .537), iterations, 100),
            Measure("append-10-mapped-samples", () =>
            {
                for (var i = 0; i < 10; i++) source.Append(source.Count);
            }, iterations, 1)
        };
        GC.KeepAlive(source);
        GC.KeepAlive(warmup);
        var report = new
        {
            kind = "indexed-source-microbenchmark",
            warning = "One series; source operations only. Not chart rendering or an Avalonia FPS result. Sub-microsecond operations use batches of 100.",
            originalCount = count,
            finalCount = source.Count,
            pixels,
            iterations,
            model = "double X; Y selector computes sin(x*.01) + .25*sin(x*.103)",
            buildMs,
            buildSamplesPerSecond = count / (buildMs / 1000),
            buildAllocatedBytes,
            retainedBytes,
            retainedBytesPerSample = (double)retainedBytes / count,
            results,
            sink
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Index {count:N0}: build {buildMs:F1} ms; retained {retainedBytes / 1048576d:F1} MiB. {output}");
        return 0;
    }

    private static double Signal(double x) => Math.Sin(x * .01) + .25 * Math.Sin(x * .103);

    private static object Measure(string operation, Action action, int iterations, int batchSize)
    {
        for (var i = 0; i < 20; i++) action();
        var durations = new double[iterations];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var start = Stopwatch.GetTimestamp();
            for (var batch = 0; batch < batchSize; batch++) action();
            durations[iteration] = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency / batchSize;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(durations);
        return new
        {
            operation,
            batchSize,
            meanMs = durations.Average(),
            p50Ms = Percentile(durations, .50),
            p95Ms = Percentile(durations, .95),
            p99Ms = Percentile(durations, .99),
            maxMs = durations[durations.Length - 1],
            allocatedBytesPerOperation = (double)allocated / iterations / batchSize
        };
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * percentile) - 1)];
}
