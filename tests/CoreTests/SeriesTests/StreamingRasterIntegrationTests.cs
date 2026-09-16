using System;
using System.Linq;
using System.IO;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace CoreTests.SeriesTests;

[TestClass]
public class StreamingRasterIntegrationTests
{
    private int _comparison;
    private int _maximumDifference;
    [TestMethod]
    public void CoalescedMeasuresAndViewportChangesMatchUncachedPixels()
    {
        var source = new TimeSeriesBuffer<double>(x => x, x => Math.Sin(x * .04));
        source.AppendRange(Enumerable.Range(0, 500).Select(x => (double)x));
        var cached = CreateChart(source, true);
        var direct = CreateChart(source, false);
        try
        {
            using var cachedSurface = SKSurface.Create(new SKImageInfo(500, 300));
            using var directSurface = SKSurface.Create(new SKImageInfo(500, 300));
            cached.CoreChart.Measure();
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);
            for (var i = 0; i < 20; i++) source.Append(source.Count);
            cached.CoreChart.Measure();
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);
            ((StreamingLineSeries<double>)cached.Series.Single()).Stroke = new SolidColorPaint(new SKColor(0, 80, 220, 128), 2);
            ((StreamingLineSeries<double>)direct.Series.Single()).Stroke = new SolidColorPaint(new SKColor(0, 80, 220, 128), 2);
            cached.CoreChart.Measure();
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);
            for (var batch = 0; batch < 3; batch++)
            {
                for (var i = 0; i < 20; i++) source.Append(source.Count);
                cached.CoreChart.Measure();
            }
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);

            cached.XAxes.First().IsInverted = true;
            direct.XAxes.First().IsInverted = true;
            cached.YAxes.First().MaxLimit = 3;
            direct.YAxes.First().MaxLimit = 3;
            cached.CoreChart.Measure();
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);
            for (var i = 0; i < 20; i++) source.Append(source.Count);
            cached.CoreChart.Measure();
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);
            source.Clear();
            source.AppendRange(Enumerable.Range(0, 80).Select(x => (double)x));
            cached.CoreChart.Measure();
            direct.CoreChart.Measure();
            AssertMatches(cached, direct, cachedSurface, directSurface);
            // Both cropped and origin-zero intermediate 8-bit surfaces measured at most
            // 4/255 difference at antialiased edges versus drawing directly on white.
            // Preserve that measured bound; stale pixels or lost segments exceed it.
            Assert.IsTrue(_maximumDifference <= 4, $"Maximum channel difference across all cases: {_maximumDifference}.");
        }
        finally
        {
            cached.CoreChart.Unload();
            direct.CoreChart.Unload();
        }
    }

    private static SKCartesianChart CreateChart(TimeSeriesBuffer<double> source, bool cache)
    {
        var chart = new SKCartesianChart
        {
            Width = 400, Height = 240, DrawMargin = new Margin(10),
            Series = [new StreamingLineSeries<double>(source)
            {
                UseRasterCache = cache, UseOnePixelStroke = true, Stroke = new SolidColorPaint(SKColors.Red, 1)
            }],
            XAxes = [new Axis { MinLimit = 0, MaxLimit = 1000, IsVisible = false }],
            YAxes = [new Axis { MinLimit = -2, MaxLimit = 2, IsVisible = false }]
        };
        chart.CoreCanvas.DisableAnimations = true;
        return chart;
    }

    private void AssertMatches(SKCartesianChart cached, SKCartesianChart direct, SKSurface cachedSurface, SKSurface directSurface)
    {
        void Draw(SKCartesianChart chart, SKSurface surface)
        {
            surface.Canvas.ResetMatrix();
            surface.Canvas.Scale(1.25f, 1.25f);
            chart.CoreCanvas.DrawFrame(new SkiaSharpDrawingContext(chart.CoreCanvas, surface.Canvas, SKColors.White));
        }
        Draw(cached, cachedSurface);
        Draw(direct, directSurface);
        using var cachedImage = cachedSurface.Snapshot();
        using var directImage = directSurface.Snapshot();
        using var actual = SKBitmap.FromImage(cachedImage);
        using var expected = SKBitmap.FromImage(directImage);
        var actualPixels = actual.Pixels;
        var expectedPixels = expected.Pixels;
        var maxDifference = 0;
        var changedPixels = 0;
        for (var i = 0; i < actualPixels.Length; i++)
        {
            var a = actualPixels[i];
            var e = expectedPixels[i];
            var difference = Math.Max(Math.Abs(a.Alpha - e.Alpha), Math.Max(Math.Abs(a.Red - e.Red), Math.Max(Math.Abs(a.Green - e.Green), Math.Abs(a.Blue - e.Blue))));
            if (difference > 0) changedPixels++;
            maxDifference = Math.Max(maxDifference, difference);
        }
        _comparison++;
        _maximumDifference = Math.Max(_maximumDifference, maxDifference);
        Console.WriteLine($"Raster comparison {_comparison}: max RGBA difference {maxDifference}; changed pixels {changedPixels}/{actualPixels.Length}.");
        if (maxDifference > 2)
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "raster-integration-differences");
            Directory.CreateDirectory(directory);
            using var actualData = cachedImage.Encode(SKEncodedImageFormat.Png, 100);
            using var expectedData = directImage.Encode(SKEncodedImageFormat.Png, 100);
            using var actualFile = File.Create(Path.Combine(directory, $"case{_comparison}-actual.png"));
            using var expectedFile = File.Create(Path.Combine(directory, $"case{_comparison}-expected.png"));
            actualData.SaveTo(actualFile);
            expectedData.SaveTo(expectedFile);
        }
    }
}
