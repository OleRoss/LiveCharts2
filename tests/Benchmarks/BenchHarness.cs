using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace Benchmarks;

// Shared surface-allocation + draw helper so each per-series bench file only has to say
// *what* to render, not how.
// DrawOnCanvas is image export: it resets first-draw state and unloads after each call.
// Every suite using this helper therefore measures export, not retained UI updates.
internal static class BenchHarness
{
    public const int Width = 1000;
    public const int Height = 600;

    public static void Render(InMemorySkiaSharpChart chart)
    {
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height))
            ?? throw new InvalidOperationException("Could not allocate SKSurface");
        chart.DrawOnCanvas(surface.Canvas);
    }
}
