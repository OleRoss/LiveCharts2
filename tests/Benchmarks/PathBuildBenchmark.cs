using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;

namespace Benchmarks;

// Isolates native path construction from selection, scaling, rasterization and UI scheduling.
internal static class PathBuildBenchmark
{
    public static int Run(ReadOnlySpan<string> args)
    {
        var iterations = args.Length > 0 ? int.Parse(args[0]) : 1000;
        var output = args.Length > 1 ? args[1] : "artifacts/path-build.json";
        const int seriesCount = 10;
        const int pointsPerSeries = 4000;
        var points = Enumerable.Range(0, seriesCount).Select(s => Enumerable.Range(0, pointsPerSeries)
            .Select(i => new SKPoint(i * 1000f / pointsPerSeries,
                (float)(300 + 20 * s + 20 * Math.Sin(i * .08) + 8 * Math.Sin(i * .73))))
            .ToArray()).ToArray();
        var paths = Enumerable.Range(0, seriesCount).Select(_ => new SKPath()).ToArray();
        void Build(bool bulk)
        {
            for (var s = 0; s < seriesCount; s++)
            {
                var path = paths[s];
                path.Rewind();
                if (bulk) path.AddPoly(points[s], false);
                else
                {
                    path.MoveTo(points[s][0]);
                    for (var i = 1; i < pointsPerSeries; i++) path.LineTo(points[s][i]);
                }
            }
        }
        SKColor[] Raster()
        {
            using var bitmap = new SKBitmap(1000, 600);
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.Clear(SKColors.White);
            foreach (var path in paths) canvas.DrawPath(path, paint);
            return bitmap.Pixels;
        }
        try
        {
            Build(false);
            var expectedPixels = Raster();
            Build(true);
            var bulkPixels = Raster();
            if (!expectedPixels.SequenceEqual(bulkPixels))
                throw new InvalidOperationException("Bulk and individual paths must rasterize identically.");
            var results = new List<object>();
            foreach (var bulk in new[] { false, true })
            {
                for (var warmup = 0; warmup < 100; warmup++) Build(bulk);
                var durations = new double[iterations];
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    var started = Stopwatch.GetTimestamp();
                    Build(bulk);
                    durations[iteration] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Array.Sort(durations);
                results.Add(new
                {
                    operation = bulk ? "AddPoly-cached-exact-length-array" : "MoveTo-LineTo-per-point",
                    meanMs = durations.Average(),
                    p50Ms = durations[(int)(iterations * .50)],
                    p95Ms = durations[Math.Min(iterations - 1, (int)(iterations * .95))],
                    p99Ms = durations[Math.Min(iterations - 1, (int)(iterations * .99))],
                    allocatedBytesPerIteration = (double)allocated / iterations
                });
            }
            var report = new
            {
                kind = "path-construction-only",
                warning = "Precomputed pixel coordinates and stable point count. Excludes source selection, coordinate scaling, draw time, gap runs and UI scheduling.",
                seriesCount, pointsPerSeries, iterations,
                identicalRaster = true,
                results
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        finally
        {
            foreach (var path in paths) path.Dispose();
        }
    }
}
