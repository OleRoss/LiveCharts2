using System;
using System.Linq;
using System.Runtime.CompilerServices;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace CoreTests.SeriesTests;

[TestClass]
public class StreamingLineSeriesUnloadTests
{
    [TestMethod]
    public void UnloadReleasesOnlyItsOwnPathsAndReloadCreatesFreshState()
    {
        var series = NewSeries();
        var first = NewChart(series, 160);
        var second = NewChart(series, 240);
        try
        {
            using (var image = first.GetImage()) { }
            using (var image = second.GetImage()) { }
            var firstGeometry = Geometry(series, first);
            var secondGeometry = Geometry(series, second);
            Assert.AreNotSame(firstGeometry, secondGeometry);
            Assert.AreNotEqual(IntPtr.Zero, firstGeometry.Path.Handle);
            Assert.AreNotEqual(IntPtr.Zero, secondGeometry.Path.Handle);

            first.CoreChart.Unload();

            Assert.AreEqual(IntPtr.Zero, firstGeometry.Path.Handle, "Unload must dispose native paths and their raster cache.");
            Assert.AreNotEqual(IntPtr.Zero, secondGeometry.Path.Handle, "Another view sharing this series must retain its state.");
            series.Source.Append(1000);
            using (var image = second.GetImage()) { }
            Assert.AreSame(secondGeometry, Geometry(series, second));
            using (var image = first.GetImage()) { }
            var reloadedGeometry = Geometry(series, first);
            Assert.AreNotSame(firstGeometry, reloadedGeometry);
            Assert.AreNotEqual(IntPtr.Zero, reloadedGeometry.Path.Handle);
        }
        finally
        {
            first.CoreChart.Unload();
            second.CoreChart.Unload();
        }
    }

    [TestMethod]
    public void RetainedSeriesDoesNotKeepAnUnloadedViewAlive()
    {
        var series = NewSeries();
        var reference = CreateUnloadedView(series);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.IsFalse(reference.IsAlive, "The application's retained series must not retain an unloaded chart view.");
        GC.KeepAlive(series);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnloadedView(StreamingLineSeries<double> series)
    {
        var chart = NewChart(series, 160);
        using (var image = chart.GetImage()) { }
        chart.CoreChart.Unload();
        // Platform views stop their collection observers on unload. Detach that independent
        // observer here so this assertion specifically detects retained renderer state.
        chart.Series = Array.Empty<ISeries>();
        return new WeakReference(chart);
    }

    private static StreamingLineGeometry Geometry(StreamingLineSeries<double> series, SKCartesianChart chart) =>
        series.Stroke!.GetGeometries(chart.CoreCanvas).OfType<StreamingLineGeometry>().Single();

    private static StreamingLineSeries<double> NewSeries()
    {
        var source = new TimeSeriesBuffer<double>(x => x, x => Math.Sin(x * .05));
        source.AppendRange(Enumerable.Range(0, 1000).Select(i => (double)i));
        return new StreamingLineSeries<double>(source)
        {
            Stroke = new SolidColorPaint(SKColors.Red, 1),
            UseOnePixelStroke = true,
            UseRasterCache = true
        };
    }

    private static SKCartesianChart NewChart(StreamingLineSeries<double> series, int width) => new()
    {
        Width = width,
        Height = 120,
        ExplicitDisposing = true,
        AutoUpdateEnabled = false,
        Series = new[] { series },
        XAxes = new[] { new Axis { MinLimit = 0, MaxLimit = 1100, IsVisible = false } },
        YAxes = new[] { new Axis { MinLimit = -2, MaxLimit = 2, IsVisible = false } }
    };
}
