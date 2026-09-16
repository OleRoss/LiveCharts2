using System.Reflection;
using System.Text.Json;
using LiveChartsCore.SkiaSharpView;
using SkiaSharp;

namespace AvaloniaStreamingBench;

internal static class RasterCacheValidation
{
    // Measured for both cropped and origin-zero surfaces by the integration comparison.
    private const int ChannelTolerance = 4;

    public static void Run(string output)
    {
        var type = typeof(StreamingLineSeries<>).Assembly.GetType(
            "LiveChartsCore.SkiaSharpView.Drawing.Geometries.StreamingRasterCache", throwOnError: true)!;
        using var cache = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        var draw = type.GetMethod("Draw", BindingFlags.Instance | BindingFlags.Public)!.CreateDelegate<DrawCached>(cache);
        using var paint = new SKPaint { Color = SKColors.SteelBlue, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        var cases = new List<object>();
        long revision = 0;

        Check("initial-125percent", 340, 0, false, 1.25f, new SKRect(10, 10, 390, 160));
        Check("append-tail", 365, 330, true, 1.25f, new SKRect(10, 10, 390, 160));
        Check("replace-tail", 370, 330, true, 1.25f, new SKRect(10, 10, 390, 160), tailOffset: 15);
        paint.Color = SKColors.DarkOrange;
        Check("mutated-color", 370, 330, true, 1.25f, new SKRect(10, 10, 390, 160), tailOffset: 15);
        paint.StrokeWidth = 3;
        paint.StrokeJoin = SKStrokeJoin.Round;
        Check("mutated-width-and-join", 370, 330, true, 1.25f, new SKRect(10, 10, 390, 160), tailOffset: 15);
        Check("dpi-change", 370, 330, true, 1.5f, new SKRect(10, 10, 390, 160), tailOffset: 15);
        Check("viewport-resize", 370, 330, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 15);
        paint.Color = new SKColor(30, 120, 180, 140);
        Check("translucent-color", 370, 330, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 15);
        Check("tail-gap", 370, 305, true, 1.5f, new SKRect(20, 20, 350, 145), gap: true);
        using var replacementPaint = new SKPaint { Color = SKColors.SeaGreen, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
        Check("replacement-paint", 370, 305, true, 1.5f, new SKRect(20, 20, 350, 145), replacement: replacementPaint);
        replacementPaint.BlendMode = SKBlendMode.Multiply;
        Check("unsupported-blend-fallback", 375, 330, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 20, replacement: replacementPaint);
        replacementPaint.BlendMode = SKBlendMode.SrcOver;
        Check("return-from-fallback", 380, 375, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 20, replacement: replacementPaint);
        using var firstShader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(400, 0),
            new[] { SKColors.Red, SKColors.Blue }, SKShaderTileMode.Clamp);
        replacementPaint.Shader = firstShader;
        Check("gradient-shader", 380, 375, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 20, replacement: replacementPaint);
        replacementPaint.Shader = null;
        firstShader.Dispose();
        using var secondShader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(400, 0),
            new[] { SKColors.Yellow, SKColors.Green }, SKShaderTileMode.Clamp);
        replacementPaint.Shader = secondShader;
        Check("replacement-shader", 380, 375, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 20, replacement: replacementPaint);
        using var filter = SKColorFilter.CreateBlendMode(new SKColor(180, 150, 240), SKBlendMode.Modulate);
        replacementPaint.ColorFilter = filter;
        Check("color-filter", 380, 375, true, 1.5f, new SKRect(20, 20, 350, 145), tailOffset: 20, replacement: replacementPaint);

        cache.Dispose();
        using var disposedSurface = SKSurface.Create(new SKImageInfo(10, 10));
        using var emptyPath = new SKPath();
        var rejectedDisposedUse = false;
        try { draw(disposedSurface.Canvas, emptyPath, paint, new SKRect(0, 0, 10, 10), ++revision, 0, false); }
        catch (ObjectDisposedException) { rejectedDisposedUse = true; }
        if (!rejectedDisposedUse) throw new InvalidOperationException("Disposed cache accepted further drawing.");

        var result = new { tolerancePerChannel = ChannelTolerance, cases, rejectedDisposedUse };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);

        void Check(string name, int maxX, float dirtyX, bool prefix, float scale, SKRect viewport, float tailOffset = 0, bool gap = false, SKPaint? replacement = null)
        {
            using var path = new SKPath();
            path.MoveTo(0, 80);
            for (var x = 1; x <= maxX; x++)
            {
                var y = 80 + 30 * MathF.Sin(x * .08f) + (x > 335 ? tailOffset : 0);
                if (gap && x == 321) path.MoveTo(x, y);
                else if (!gap || x < 310 || x > 321) path.LineTo(x, y);
            }
            using var actual = SKSurface.Create(new SKImageInfo(640, 320));
            using var expected = SKSurface.Create(new SKImageInfo(640, 320));
            Prepare(actual.Canvas);
            Prepare(expected.Canvas);
            var activePaint = replacement ?? paint;
            draw(actual.Canvas, path, activePaint, viewport, ++revision, dirtyX, prefix);
            expected.Canvas.DrawPath(path, activePaint);
            var a = Pixels(actual);
            var b = Pixels(expected);
            var maxDifference = 0;
            var differentPixels = 0;
            long summedMaximumChannelDifference = 0;
            for (var i = 0; i < a.Length; i++)
            {
                var difference = Math.Max(Math.Abs(a[i].Alpha - b[i].Alpha),
                    Math.Max(Math.Abs(a[i].Red - b[i].Red),
                        Math.Max(Math.Abs(a[i].Green - b[i].Green), Math.Abs(a[i].Blue - b[i].Blue))));
                maxDifference = Math.Max(maxDifference, difference);
                summedMaximumChannelDifference += difference;
                if (difference != 0) differentPixels++;
            }
            cases.Add(new { name, maxDifference, differentPixels, pixels = a.Length,
                meanMaximumChannelDifference = summedMaximumChannelDifference / (double)a.Length });
            if (maxDifference > ChannelTolerance)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                Save(actual, Path.ChangeExtension(output, $"{name}.actual.png"));
                Save(expected, Path.ChangeExtension(output, $"{name}.expected.png"));
                File.WriteAllText(output, JsonSerializer.Serialize(new { status = "failed", tolerancePerChannel = ChannelTolerance, cases },
                    new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidOperationException($"Raster cache {name} differs by {maxDifference} in a channel.");
            }

            void Prepare(SKCanvas canvas)
            {
                canvas.Clear(new SKColor(245, 242, 237));
                canvas.Scale(scale);
                canvas.Translate(3, 2);
                canvas.ClipRect(viewport);
            }
        }
    }

    private static SKColor[] Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.Pixels;
    }

    private static void Save(SKSurface surface, string path)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    private delegate void DrawCached(SKCanvas destination, SKPath path, SKPaint paint, SKRect bounds,
        long revision, float dirtyFromX, bool allowPrefixReuse);
}
