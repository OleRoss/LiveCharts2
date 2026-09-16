using System;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Measure;
using LiveChartsCore.Motion;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace CoreTests.SeriesTests;

[TestClass]
public class StreamingViewportAnimationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnimatedPanTracksDisplayedFrameForTooltipsAndRefinesTheUnionWhenSettled(bool rasterCache)
    {
        var chart = CreateChart(rasterCache);
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height));
        try
        {
            CoreMotionCanvas.DebugElapsedMilliseconds = 0;
            chart.CoreChart.Measure();
            CoreMotionCanvas.DebugElapsedMilliseconds = 2000;
            Draw(chart, surface);
            var series = (StreamingLineSeries<double>)chart.Series.Single();
            var geometry = series.Stroke!.GetGeometries(chart.CoreCanvas).OfType<StreamingLineGeometry>().Single();
            chart.XAxes.First().MinLimit = 40;
            chart.XAxes.First().MaxLimit = 140;
            chart.CoreChart.Measure();
            Assert.IsTrue(geometry.Path.Bounds.Left < chart.CoreChart.DrawMarginLocation.X - 50,
                "Selection must retain the old visible edge while the viewport moves.");

            CoreMotionCanvas.DebugElapsedMilliseconds = 2250;
            Draw(chart, surface);
            Assert.AreEqual(60d, HitAtCenter(chart), .01, "Quarter-way viewport is 10..110, not target 40..140.");
            Assert.IsFalse(geometry.IsValid, "The streaming geometry must request subsequent animation frames.");
            using (var image = surface.Snapshot())
            using (var bitmap = SKBitmap.FromImage(image))
            {
                var px = (int)Math.Round(geometry.DrawnXScale!.ToPixels(25));
                var py = (int)Math.Round(geometry.DrawnYScale!.ToPixels(25));
                var redPixelFound = false;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var color = bitmap.GetPixel(px + dx, py + dy);
                    redPixelFound |= color.Red > 150 && color.Green < 150;
                }
                Assert.IsTrue(redPixelFound, "The path pixels must move with the actual viewport, including samples outside the target viewport.");
            }
            CoreMotionCanvas.DebugElapsedMilliseconds = 2500;
            Assert.AreEqual(60d, HitAtCenter(chart), .01, "Hit tests must match the last displayed frame, not an undrawn future frame.");
            Draw(chart, surface);
            Assert.AreEqual(70d, HitAtCenter(chart), .01);

            CoreMotionCanvas.DebugElapsedMilliseconds = 3100;
            Draw(chart, surface);
            Assert.AreEqual(90d, HitAtCenter(chart), .01);
            Assert.IsFalse(geometry.NeedsRefinement);
            Assert.IsTrue(geometry.Path.Bounds.Left > chart.CoreChart.DrawMarginLocation.X - 10,
                "Settled selection must recover target viewport resolution without another measure.");
        }
        finally
        {
            chart.CoreChart.Unload();
            CoreMotionCanvas.DebugElapsedMilliseconds = -1;
        }
    }

    [TestMethod]
    public void DisableAnimationsUsesTargetViewportImmediately()
    {
        var chart = CreateChart();
        using var surface = SKSurface.Create(new SKImageInfo(chart.Width, chart.Height));
        try
        {
            CoreMotionCanvas.DebugElapsedMilliseconds = 0;
            chart.CoreChart.Measure();
            CoreMotionCanvas.DebugElapsedMilliseconds = 2000;
            Draw(chart, surface);
            chart.CoreCanvas.DisableAnimations = true;
            chart.XAxes.First().MinLimit = 40;
            chart.XAxes.First().MaxLimit = 140;
            chart.CoreChart.Measure();
            Draw(chart, surface);
            Assert.AreEqual(90d, HitAtCenter(chart), .01);
        }
        finally
        {
            chart.CoreChart.Unload();
            CoreMotionCanvas.DebugElapsedMilliseconds = -1;
        }
    }

    private static SKCartesianChart CreateChart(bool rasterCache = false)
    {
        var source = new TimeSeriesBuffer<double>(x => x, x => x);
        source.AppendRange(Enumerable.Range(0, 201).Select(i => (double)i));
        return new SKCartesianChart
        {
            Width = 400, Height = 240, DrawMargin = new Margin(10),
            EasingFunction = EasingFunctions.Lineal, AnimationsSpeed = TimeSpan.FromMilliseconds(1000),
            Series = [new StreamingLineSeries<double>(source) { Stroke = new SolidColorPaint(SKColors.Red, 1), UseOnePixelStroke = true, UseRasterCache = rasterCache }],
            XAxes = [new Axis { MinLimit = 0, MaxLimit = 100, LabelsPaint = null, SeparatorsPaint = null }],
            YAxes = [new Axis { MinLimit = -1, MaxLimit = 201, LabelsPaint = null, SeparatorsPaint = null }]
        };
    }

    private static void Draw(SKCartesianChart chart, SKSurface surface) =>
        chart.CoreCanvas.DrawFrame(new SkiaSharpDrawingContext(chart.CoreCanvas, surface.Canvas, SKColors.White));

    private static double HitAtCenter(SKCartesianChart chart)
    {
        var location = chart.CoreChart.DrawMarginLocation;
        var size = chart.CoreChart.DrawMarginSize;
        return (double)chart.CoreChart.FindHoveredPointsBy(new LvcPoint(location.X + size.Width / 2, location.Y + size.Height / 2))
            .Single().Context.DataSource!;
    }
}
