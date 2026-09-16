using System.Diagnostics;
using System.Text.Json;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace Benchmarks;

// A synchronous retained-chart CPU benchmark. Calls the same Measure used
// by the UI update dispatcher, without counting asynchronous scheduling as completed work.
// This is NOT an Avalonia frame-rate measurement and deliberately disables animations.
internal static class StreamingBenchmark
{
    public static int Run(ReadOnlySpan<string> args)
    {
        var pointsPerSeries = args.Length > 0 ? int.Parse(args[0]) : 1_000;
        var iterations = args.Length > 1 ? int.Parse(args[1]) : 30;
        var output = args.Length > 2 ? args[2] : "artifacts/stream-baseline.json";
        var mode = args.Length > 3 ? args[3] : "stripped";
        var viewport = args.Length > 4 ? args[4] : "full";
        if (pointsPerSeries < 1 || iterations < 1) throw new ArgumentOutOfRangeException(nameof(args));
        if (viewport is not ("full" or "latest")) throw new ArgumentException("Viewport must be full or latest.");
        if (mode is not ("default" or "stripped" or "indexed-line" or "streaming"))
            throw new ArgumentException("Mode must be default, stripped, indexed-line or streaming.");
        const int seriesCount = 10;
        const int samplesPerTick = 10;
        var indexed = mode is "indexed-line" or "streaming";
        var setupWatch = Stopwatch.StartNew();
        var values = Enumerable.Range(0, seriesCount).Select(s => indexed
            ? new List<double>()
            : Enumerable.Range(0, pointsPerSeries).Select(i => Sample(i, s)).ToList()).ToArray();
        var buffers = Enumerable.Range(0, seriesCount).Select(s => new TimeSeriesBuffer<(double X, double Y)>(p => p.X, p => p.Y)).ToArray();
        if (indexed)
            for (var s = 0; s < seriesCount; s++)
                for (var i = 0; i < pointsPerSeries; i++)
                    buffers[s].Append((i, Sample(i, s)));
        var sourceBuildMs = setupWatch.Elapsed.TotalMilliseconds;
        var representatives = Enumerable.Range(0, seriesCount).Select(_ => new List<Coordinate>()).ToArray();
        var selected = new List<int>();
        void SelectRepresentatives()
        {
            if (mode != "indexed-line") return;
            for (var s = 0; s < seriesCount; s++)
            {
                buffers[s].Select(viewport == "latest" ? Math.Max(0, buffers[s].Count - 10000) : 0,
                    buffers[s].Count - 1, BenchHarness.Width, selected);
                representatives[s].Clear();
                foreach (var index in selected) representatives[s].Add(buffers[s].GetSample(index).Coordinate);
            }
        }
        SelectRepresentatives();
        ISeries CreateSeries(int s)
        {
            var paint = new SolidColorPaint(new SKColor((byte)(40 + s * 20), 90, 150), 1);
            if (mode == "streaming") return new StreamingLineSeries<(double X, double Y)>(buffers[s]) { Stroke = paint };
            if (mode == "indexed-line") return new LineSeries<Coordinate>
            {
                Values = representatives[s], Mapping = (p, _) => p,
                GeometrySize = 0, Fill = null, LineSmoothness = 0, Stroke = paint
            };
            return mode == "default" ? new LineSeries<double> { Values = values[s] }
                : new LineSeries<double> { Values = values[s], GeometrySize = 0, Fill = null, LineSmoothness = 0, Stroke = paint };
        }
        var chart = new SKCartesianChart
        {
            Width = BenchHarness.Width,
            Height = BenchHarness.Height,
            Series = Enumerable.Range(0, seriesCount).Select(CreateSeries).ToArray(),
            YAxes = [new Axis { MinLimit = -2, MaxLimit = 12 }]
        };
        void UpdateViewport()
        {
            if (viewport != "latest") return;
            var count = indexed ? buffers[0].Count : values[0].Count;
            var axis = chart.XAxes.First();
            axis.MinLimit = Math.Max(0, count - 10000);
            axis.MaxLimit = count - 1;
        }
        UpdateViewport();
        chart.CoreCanvas.DisableAnimations = true;
        Action measure = chart.CoreChart.Measure;
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height))!;
        var context = new SkiaSharpDrawingContext(chart.CoreCanvas, surface.Canvas, SKColors.White);
        var watch = Stopwatch.StartNew();
        measure();
        chart.CoreCanvas.DrawFrame(context);
        var firstRenderMs = watch.Elapsed.TotalMilliseconds;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var preview = surface.Snapshot())
        using (var png = preview.Encode(SKEncodedImageFormat.Png, 100))
        using (var imageOutput = File.Create(Path.ChangeExtension(output, ".png")))
            png.SaveTo(imageOutput);
        var frames = new List<object>();
        var totals = new List<double>();
        var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        for (var tick = -3; tick < iterations; tick++)
        {
            watch.Restart();
            for (var s = 0; s < seriesCount; s++)
                for (var j = 0; j < samplesPerTick; j++)
                    if (indexed) buffers[s].Append((buffers[s].Count, Sample(buffers[s].Count, s)));
                    else values[s].Add(Sample(values[s].Count, s));
            var ingestMs = watch.Elapsed.TotalMilliseconds;
            UpdateViewport();
            SelectRepresentatives();
            var selectionMs = watch.Elapsed.TotalMilliseconds - ingestMs;
            measure();
            var measureMs = watch.Elapsed.TotalMilliseconds - ingestMs - selectionMs;
            chart.CoreCanvas.DrawFrame(context);
            var drawFinishedMs = watch.Elapsed.TotalMilliseconds;
            var hitCount = chart.CoreChart.FindHoveredPointsBy(new LvcPoint(chart.Width / 2, chart.Height / 2)).Count();
            var totalMs = watch.Elapsed.TotalMilliseconds;
            if (tick < 0) continue;
            totals.Add(totalMs);
            frames.Add(new { tick, ingestMs, selectionMs, measureMs, drawMs = drawFinishedMs - ingestMs - selectionMs - measureMs, hitTestMs = totalMs - drawFinishedMs, hitCount, totalMs });
        }
        totals.Sort();
        double Percentile(double fraction) => totals[(int)Math.Ceiling(fraction * totals.Count) - 1];
        var result = new
        {
            kind = "retained-core-cpu-skia",
            limitations = "No Avalonia compositor, input dispatcher, animation or 10 ms wall-clock pacing. Each iteration appends one 100-sample batch and measures/draws synchronously.",
            timestamp = DateTimeOffset.UtcNow,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            pointsPerSeries, seriesCount, samplesPerTick, iterations, mode, viewport,
            width = chart.Width, height = chart.Height, sourceBuildMs, firstRenderMs,
            meanMs = totals.Average(), p50Ms = Percentile(.50), p95Ms = Percentile(.95), p99Ms = Percentile(.99), maxMs = totals[^1],
            frameBudgetMs = 1000.0 / 60,
            overBudgetCount = totals.Count(t => t > 1000.0 / 60),
            allocatedBytesIncludingWarmup = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
            gcCollectionsIncludingWarmup = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - gcBefore[g]).ToArray(),
            workingSetBytes = Environment.WorkingSet,
            frames
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{pointsPerSeries:N0} points/series × {seriesCount}: mean {totals.Average():F2} ms; p95 {Percentile(.95):F2} ms; {totals.Count(t => t > 1000.0 / 60)}/{iterations} over 16.67 ms. {output}");
        chart.CoreChart.Unload();
        return Percentile(.95) <= 1000.0 / 60 ? 0 : 2;
    }

    private static double Sample(int index, int series) => Math.Sin(index * .005) + series;
}
