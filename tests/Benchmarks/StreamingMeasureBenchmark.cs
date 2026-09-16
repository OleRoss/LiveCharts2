using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace Benchmarks;

// Diagnostic attribution of actual chart.Measure work. No rasterization or UI FPS claim.
internal static class StreamingMeasureBenchmark
{
    public static int Run(ReadOnlySpan<string> args)
    {
        var count = args.Length > 0 ? int.Parse(args[0]) : 1_000_000;
        var iterations = args.Length > 1 ? int.Parse(args[1]) : 300;
        var output = args.Length > 2 ? args[2] : "artifacts/streaming-measure-profile.json";
        const int seriesCount = 10;
        var sources = Enumerable.Range(0, seriesCount)
            .Select(_ => new TimeSeriesBuffer<(double X, double Y)>(p => p.X, p => p.Y)).ToArray();
        for (var s = 0; s < seriesCount; s++)
            for (var i = 0; i < count; i++) sources[s].Append((i, Sample(i, s)));
        var series = sources.Select(source => new StreamingLineSeries<(double X, double Y)>(source)
        {
            Stroke = new SolidColorPaint(SKColors.Blue, 1),
            UseOnePixelStroke = true
        }).ToArray();
        foreach (var item in series) Set(item, "EnablePerformanceDiagnostics", true);
        var chart = new SKCartesianChart
        {
            Width = 1000,
            Height = 600,
            Series = series,
            XAxes = [new Axis { MinLimit = 0, MaxLimit = count + (iterations + 100) * 10 }],
            YAxes = [new Axis { MinLimit = -2, MaxLimit = 12 }]
        };
        chart.CoreCanvas.DisableAnimations = true;
        void Append()
        {
            for (var s = 0; s < seriesCount; s++)
                for (var i = 0; i < 10; i++)
                {
                    var index = sources[s].Count;
                    sources[s].Append((index, Sample(index, s)));
                }
        }
        long Total(string name) => series.Sum(item => Read(item, name));
        var names = new[] { "FullSelectionCount", "PartialSelectionCount", "UnchangedSelectionCount", "SelectionElapsedTicks", "PathBuildElapsedTicks" };
        try
        {
            for (var warmup = 0; warmup < 20; warmup++)
            {
                Append();
                chart.CoreChart.Measure();
            }
            var before = names.ToDictionary(name => name, Total);
            var durations = new double[iterations];
            var allocatedBytes = new long[iterations];
            for (var i = 0; i < iterations; i++)
            {
                Append();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                chart.CoreChart.Measure();
                durations[i] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                allocatedBytes[i] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            }
            var counters = names.ToDictionary(name => name, name => Total(name) - before[name]);
            var selectionMean = counters["SelectionElapsedTicks"] * 1000d / Stopwatch.Frequency / iterations;
            var pathMean = counters["PathBuildElapsedTicks"] * 1000d / Stopwatch.Frequency / iterations;
            var mean = durations.Average();
            Array.Sort(durations);
            var report = new
            {
                kind = "actual-streaming-measure-attribution",
                warning = "Fixed preallocated X viewport; ten series append ten samples before each synchronous Measure. No chart drawing, UI thread scheduling or FPS measurement. Diagnostic stopwatches are enabled.",
                pointsPerSeries = count,
                signal = "series + sin(sampleIndex * 0.005), matching Avalonia target waveform",
                seriesCount,
                iterations,
                counters,
                totalMeasureMeanMs = mean,
                allocatedBytesPerMeasureMean = allocatedBytes.Average(),
                allocatedBytes,
                selectionMeanMs = selectionMean,
                pathBuildMeanMs = pathMean,
                otherChartMeasureMeanMs = mean - selectionMean - pathMean,
                p50Ms = durations[(int)(iterations * .50)],
                p95Ms = durations[Math.Min(iterations - 1, (int)(iterations * .95))],
                p99Ms = durations[Math.Min(iterations - 1, (int)(iterations * .99))],
                displayedRepresentatives = series.Select(item => item.DisplayedPointCount).ToArray()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        finally
        {
            chart.CoreChart.Unload();
        }
    }

    private static double Sample(double x, int series) => series + Math.Sin(x * .005);

    private static long Read(object target, string name)
    {
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var value = type.GetProperty(name, flags)?.GetValue(target) ?? type.GetField(name, flags)?.GetValue(target);
        return value is long count ? count : throw new InvalidOperationException($"Missing diagnostic {name} on {type.Name}.");
    }

    private static void Set(object target, string name, bool value)
    {
        var type = target.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (type.GetProperty(name, flags) is { } property) property.SetValue(target, value);
        else if (type.GetField(name, flags) is { } field) field.SetValue(target, value);
        else throw new InvalidOperationException($"Missing diagnostic {name} on {type.Name}.");
    }
}
