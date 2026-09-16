using System;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace CoreTests.SeriesTests;

[TestClass]
public class StreamingLineSeriesTests
{
    [TestMethod]
    public void RepeatedMeasuresBoundVisualsAndReturnTheOriginalTooltipModel()
    {
        var mappings = 0;
        var source = new TimeSeriesBuffer<Measurement>(m => { mappings++; return m.X; }, m => m.Y);
        var measurements = Enumerable.Range(0, 100_000).Select(i => new Measurement(i, Math.Sin(i))).ToArray();
        source.AppendRange(measurements);
        var series = new StreamingLineSeries<Measurement>(source);
        var chart = NewChart(series, 99_999);
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height));
        for (var i = 0; i < 3; i++)
        {
            chart.CoreChart.Measure();
            chart.CoreCanvas.DrawFrame(new SkiaSharpDrawingContext(chart.CoreCanvas, surface.Canvas, SKColors.White));
        }
        Assert.AreEqual(100_000, mappings, "Panning and remeasurement must not remap history.");
        Assert.IsTrue(series.DisplayedPointCount <= chart.Width * 5 + 2);
        var xScale = chart.XAxes.First().GetNextScaler((CartesianChartEngine)chart.CoreChart);
        var pointer = new LvcPoint(xScale.ToPixels(50_123), chart.CoreChart.DrawMarginLocation.Y + chart.CoreChart.DrawMarginSize.Height / 2);
        var hit = chart.CoreChart.HitTestSeries(series, pointer, FindingStrategy.CompareOnlyXTakeClosest, FindPointFor.HoverEvent).Single();
        Assert.AreEqual(50_123, hit.Index);
        Assert.AreSame(measurements[50_123], hit.Context.DataSource);
        Assert.AreEqual(measurements[50_123].Y, hit.Coordinate.PrimaryValue);
        source.Clear();
        source.AppendRange(measurements);
        Assert.AreEqual(0, chart.CoreChart.HitTestSeries(series, pointer,
            FindingStrategy.CompareOnlyXTakeClosest, FindPointFor.HoverEvent).Count(),
            "A source reset cannot expose new models against an old rendered frame.");
        chart.CoreChart.Measure();
        Assert.AreEqual(1, chart.CoreChart.HitTestSeries(series, pointer,
            FindingStrategy.CompareOnlyXTakeClosest, FindPointFor.HoverEvent).Count());
        chart.CoreChart.Unload();
    }

    [TestMethod]
    public void CachedViewportMatchesFreshRenderingAcrossAppendsGapsAndInvalidations()
    {
        var source = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        void Append(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var index = source.Count;
                source.Append(new Measurement(index / 3, index % 29 == 0 ? double.NaN : Math.Sin(index * .19)));
            }
        }
        Append(900);
        var cachedSeries = new StreamingLineSeries<Measurement>(source) { Stroke = new SolidColorPaint(SKColors.Red, 1) };
        var cached = NewChart(cachedSeries, 1000);
        cached.Width = 160;
        cached.Height = 120;
        var minX = 0d;
        var maxX = 1000d;
        var inverted = false;

        void AssertMatchesFresh(string scenario)
        {
            var cachedAxis = cached.XAxes.First();
            cachedAxis.MinLimit = minX;
            cachedAxis.MaxLimit = maxX;
            cachedAxis.IsInverted = inverted;
            var freshSeries = new StreamingLineSeries<Measurement>(source) { Stroke = new SolidColorPaint(SKColors.Red, 1) };
            var fresh = NewChart(freshSeries, maxX);
            fresh.Width = cached.Width;
            fresh.Height = cached.Height;
            fresh.XAxes.First().MinLimit = minX;
            fresh.XAxes.First().IsInverted = inverted;
            try
            {
                var actual = RenderPixels(cached);
                var expected = RenderPixels(fresh);
                Assert.AreEqual(freshSeries.DisplayedPointCount, cachedSeries.DisplayedPointCount, scenario);
                CollectionAssert.AreEqual(expected, actual, scenario);
            }
            finally
            {
                fresh.CoreChart.Unload();
            }
        }

        try
        {
            AssertMatchesFresh("Initial fixed viewport");
            for (var batch = 0; batch < 10; batch++)
            {
                Append(31);
                AssertMatchesFresh($"Append batch {batch}, including duplicate X and gaps");
            }
            AssertMatchesFresh("Repeated measure without appending");
            minX = 80;
            maxX = 400;
            AssertMatchesFresh("Pan and zoom");
            cached.Width = 273;
            AssertMatchesFresh("Resize changes bucket count");
            inverted = true;
            AssertMatchesFresh("Inverse X axis");
            maxX = 280;
            Append(100);
            AssertMatchesFresh("Appends after the visible interval");
            source.Clear();
            AssertMatchesFresh("Clear to empty");
            Append(900);
            AssertMatchesFresh("Repopulate after clear");
        }
        finally
        {
            cached.CoreChart.Unload();
        }
    }

    [TestMethod]
    public void SoftwarePathDoesNotJoinAcrossMissingSamplesAndCanBeReattached()
    {
        var source = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
        source.AppendRange(Enumerable.Range(0, 7).Select(i => new Measurement(i, i == 3 ? double.NaN : 0)));
        var series = new StreamingLineSeries<Measurement>(source) { Stroke = new SolidColorPaint(SKColors.Red, 3) };
        var chart = NewChart(series, 6);
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height));
        for (var run = 0; run < 2; run++)
        {
            chart.CoreChart.Measure();
            chart.CoreCanvas.DrawFrame(new SkiaSharpDrawingContext(chart.CoreCanvas, surface.Canvas, SKColors.White));
            var core = (CartesianChartEngine)chart.CoreChart;
            var xScale = chart.XAxes.First().GetNextScaler(core);
            var yScale = chart.YAxes.First().GetNextScaler(core);
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            var line = bitmap.GetPixel((int)xScale.ToPixels(1), (int)yScale.ToPixels(0));
            var gap = bitmap.GetPixel((int)xScale.ToPixels(3), (int)yScale.ToPixels(0));
            Assert.IsTrue(line.Red > 200 && line.Green < 100, "Finite segment must be drawn.");
            Assert.IsTrue(gap.Green > 200, "Missing sample must leave the background visible.");
            chart.Series = Array.Empty<ISeries>();
            chart.CoreChart.Measure();
            chart.Series = new[] { series };
        }
        chart.CoreChart.Unload();
    }

    private static SKColor[] RenderPixels(SKCartesianChart chart)
    {
        chart.CoreCanvas.DisableAnimations = true;
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height));
        chart.CoreChart.Measure();
        chart.CoreCanvas.DrawFrame(new SkiaSharpDrawingContext(chart.CoreCanvas, surface.Canvas, SKColors.White));
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.Pixels;
    }

    private static SKCartesianChart NewChart(ISeries series, double maxX) => new()
    {
        Width = 800,
        Height = 400,
        Series = new[] { series },
        XAxes = new[] { new Axis { MinLimit = 0, MaxLimit = maxX, IsVisible = false } },
        YAxes = new[] { new Axis { MinLimit = -2, MaxLimit = 2, IsVisible = false } }
    };

    private sealed class Measurement(double x, double y)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
    }
}
