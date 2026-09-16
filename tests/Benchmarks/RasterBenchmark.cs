using System.Diagnostics;
using System.Text.Json;
using LiveChartsCore.Defaults;
using SkiaSharp;

namespace Benchmarks;

// Isolates CPU rasterization: identical indexed representatives and coordinate transforms.
// DrawPoints uses independent line segments, so its join behavior differs from a path.
internal static class RasterBenchmark
{
    public static int Run(ReadOnlySpan<string> args)
    {
        var width = args.Length > 0 ? int.Parse(args[0]) : 1250;
        var height = args.Length > 1 ? int.Parse(args[1]) : 700;
        var iterations = args.Length > 2 ? int.Parse(args[2]) : 120;
        var output = args.Length > 3 ? args[3] : "artifacts/raster.json";
        var scale = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 1;
        var strokeWidths = args.Length > 5
            ? args[5].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
            : new[] { 1f, 0f, 2f };
        var algorithms = args.Length > 6 ? args[6].Split(',') : new[] { "path", "segments" };
        if (algorithms.Any(a => a is not ("path" or "segments" or "envelope")))
            throw new ArgumentException("Algorithms must be path, segments or envelope.");
        if (width < 1 || height < 1 || iterations < 1) throw new ArgumentOutOfRangeException(nameof(args));
        if (scale <= 0 || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(args));
        const int pointsPerSeries = 1_000_000;
        const int seriesCount = 10;
        var paths = new SKPath[seriesCount];
        var pairs = new SKPoint[seriesCount][];
        var envelopes = new SKRect[seriesCount][];
        var selected = new List<int>();
        var pointCounts = new int[seriesCount];
        for (var series = 0; series < seriesCount; series++)
        {
            var source = new TimeSeriesBuffer<(double X, double Y)>(p => p.X, p => p.Y);
            for (var i = 0; i < pointsPerSeries; i++) source.Append((i, Math.Sin(i * .005) + series));
            source.Select(0, pointsPerSeries - 1, width, selected);
            paths[series] = new SKPath();
            var segments = new List<SKPoint>();
            SKPoint? previous = null;
            foreach (var index in selected)
            {
                var sample = source.GetSample(index);
                if (double.IsNaN(sample.Y)) { previous = null; continue; }
                var point = new SKPoint((float)(sample.X / (pointsPerSeries - 1) * (width - 1)),
                    (float)((12 - sample.Y) / 14 * (height - 1)));
                if (previous is { } prior)
                {
                    paths[series].LineTo(point);
                    segments.Add(prior);
                    segments.Add(point);
                }
                else paths[series].MoveTo(point);
                previous = point;
            }
            pairs[series] = segments.ToArray();
            pointCounts[series] = selected.Count;
            var rectangles = new List<SKRect>();
            for (var pixel = 0; pixel < width; pixel++)
            {
                var minX = pixel * (pointsPerSeries - 1d) / width;
                var maxX = (pixel + 1d) * (pointsPerSeries - 1d) / width;
                if (!source.TryGetBounds(minX, maxX, out var minY, out var maxY)) continue;
                rectangles.Add(new SKRect(pixel, (float)((12 - maxY) / 14 * (height - 1)),
                    pixel + 1, (float)((12 - minY) / 14 * (height - 1))));
            }
            envelopes[series] = rectangles.ToArray();
        }
        var physicalWidth = (int)Math.Ceiling(width * scale);
        var physicalHeight = (int)Math.Ceiling(height * scale);
        using var surface = SKSurface.Create(new SKImageInfo(physicalWidth, physicalHeight))!;
        surface.Canvas.Scale((float)scale, (float)scale);
        using var paint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Miter };
        var results = new List<object>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        // Adjacent rows vary one parameter; both algorithms receive identical geometry.
        foreach (var strokeWidth in strokeWidths)
        foreach (var antialias in new[] { true, false })
        foreach (var algorithm in algorithms)
        {
            paint.StrokeWidth = strokeWidth;
            paint.IsAntialias = antialias;
            paint.Style = algorithm == "envelope" ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
            void Draw()
            {
                surface.Canvas.Clear(SKColors.White);
                for (var series = 0; series < seriesCount; series++)
                {
                    paint.Color = new SKColor((byte)(40 + series * 20), 90, 150);
                    if (algorithm == "path") surface.Canvas.DrawPath(paths[series], paint);
                    else if (algorithm == "segments") surface.Canvas.DrawPoints(SKPointMode.Lines, pairs[series], paint);
                    else foreach (var rectangle in envelopes[series]) surface.Canvas.DrawRect(rectangle, paint);
                }
            }
            var warmup = Stopwatch.StartNew();
            do { Draw(); } while (warmup.Elapsed.TotalSeconds < 2);
            var samples = new double[iterations];
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                var started = Stopwatch.GetTimestamp();
                Draw();
                samples[i] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            }
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var sorted = samples.Order().ToArray();
            var name = $"{algorithm}-aa{antialias}-stroke{strokeWidth}";
            var pngPath = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + name + ".png");
            using (var image = surface.Snapshot())
            using (var png = image.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(pngPath)) png.SaveTo(file);
            double Percentile(double fraction) => sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
            results.Add(new { algorithm, antialias, strokeWidth, meanMs = samples.Average(), p50Ms = Percentile(.5),
                p95Ms = Percentile(.95), p99Ms = Percentile(.99), maxMs = sorted[^1], allocatedBytes,
                pngPath, samplesMs = samples });
            Console.WriteLine($"Raster {width}x{height} scale{scale} {name}: mean {samples.Average():F3} ms; p95 {Percentile(.95):F3} ms");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            kind = "cpu-skia-raster-only", width, height, scale, physicalWidth, physicalHeight, pointsPerSeries, seriesCount, iterations, pointCounts,
            warning = "Precomputed geometry; excludes source selection, chart measurement, axes, tooltips, Avalonia and compositor. Independent segments change joins. Antialias off creates jagged edges. Envelope is a different visualization: exact min/max fills each column, omitting within-column ordering and gaps; stroke width does not affect its fill.",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var path in paths) path.Dispose();
        return 0;
    }
}
