using System;
using System.Collections.Generic;
using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace CoreTests.AxisTests;

[TestClass]
[DoNotParallelize]
public class AxisMeasurementReuseTests
{
    [TestMethod]
    public void ScratchLabelsMatchFreshLabelsAndPreserveFormatterCallsAcrossViewsAndStyles()
    {
        var first = new SKCartesianChart();
        var second = new SKCartesianChart();
        var paint = new SolidColorPaint(SKColors.Black);
        var axis = NewAxis(paint);
        var formatted = new List<double>();
        var suffix = "";
        axis.Labeler = value =>
        {
            formatted.Add(value);
            return value == 1 ? "" : value == 0 ? "long measurement label" + suffix : "x" + suffix;
        };

        for (var pass = 0; pass < 3; pass++)
        {
            suffix = new string('W', pass * 2);
            axis.TextSize = 12 + pass * 4;
            axis.LabelsRotation = pass * 30;
            axis.Padding = new Padding(pass + 1);
            paint.SKTypeface = pass == 0 ? SKTypeface.Default : SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            var scale = 1f + pass * .1f;
            paint.ConfigureSkiaSharpFont((_, typeface, size) => new SKFont(typeface, size) { ScaleX = scale });
            var chart = pass == 1 ? second : first;
            ((ICartesianAxis)axis).OnMeasureStarted(chart.CoreChart, AxisOrientation.X);
            formatted.Clear();
            RecordingLabel.Reset();

            _ = ((ICartesianAxis)axis).PossibleMaxLabelSize;
            _ = axis.GetPossibleSize(chart.CoreChart);

            CollectionAssert.AreEqual(new double[] { -1, 0, 1, 2, 3, -1, 0, 1, 2, 3 }, formatted);
            Assert.AreEqual(2, RecordingLabel.Created, "Each sizing pass needs only one scratch geometry.");
            Assert.AreEqual(10, RecordingLabel.Measured);
            Assert.AreEqual(10, RecordingLabel.Disposed);
            // Measurement cleanup must not dispose the shared paint.
            var label = new LabelGeometry { Text = "still usable", TextSize = 12, Paint = paint };
            Assert.IsTrue(label.Measure().Width > 0);
            label.OnDisposed();
        }
        first.CoreChart.Unload();
        second.CoreChart.Unload();
    }

    [TestMethod]
    public void ScratchNativeResourcesAreReleasedWhenMeasurementThrows()
    {
        var chart = new SKCartesianChart();
        var axis = NewAxis(new SolidColorPaint(SKColors.Black));
        ((ICartesianAxis)axis).OnMeasureStarted(chart.CoreChart, AxisOrientation.X);
        RecordingLabel.Reset();
        RecordingLabel.ThrowAfterMeasure = true;
        try
        {
            _ = ((ICartesianAxis)axis).PossibleMaxLabelSize;
            Assert.Fail("Expected the label measurement to throw.");
        }
        catch (InvalidOperationException) { }
        finally
        {
            RecordingLabel.ThrowAfterMeasure = false;
        }
        Assert.AreEqual(1, RecordingLabel.Created);
        Assert.AreEqual(1, RecordingLabel.Disposed);
        chart.CoreChart.Unload();
    }

    [TestMethod]
    public void ReusedLabelClearsOldTextWhenTextBecomesEmptyAndCanBeDisposedTwice()
    {
        var label = new LabelGeometry { Text = "previous label", TextSize = 16, Paint = new SolidColorPaint(SKColors.Black) };
        Assert.IsTrue(label.Measure().Width > 0);
        label.Text = "";
        Assert.AreEqual(0f, label.Measure().Width);
        label.Text = "new";
        Assert.IsTrue(label.Measure().Width > 0);
        label.OnDisposed();
        label.OnDisposed();
    }

    private static RecordingAxis NewAxis(SolidColorPaint paint) => new()
    {
        MinLimit = 0, MaxLimit = 2, MinStep = 1, ForceStepToMin = true,
        LabelsPaint = paint, TextSize = 12
    };

    private sealed class RecordingAxis : CoreAxis<RecordingLabel, LineGeometry> { }

    public sealed class RecordingLabel : LabelGeometry
    {
        public static int Created;
        public static int Measured;
        public static int Disposed;
        public static bool ThrowAfterMeasure;
        public RecordingLabel() => Created++;
        public static void Reset() { Created = Measured = Disposed = 0; }
        public override LvcSize Measure()
        {
            Measured++;
            var actual = base.Measure();
            var fresh = new LabelGeometry
            {
                Text = Text, TextSize = TextSize, Padding = Padding,
                RotateTransform = RotateTransform, Paint = Paint
            };
            try
            {
                var expected = fresh.Measure();
                Assert.AreEqual(expected.Width, actual.Width, .001f);
                Assert.AreEqual(expected.Height, actual.Height, .001f);
            }
            finally { fresh.OnDisposed(); }
            if (ThrowAfterMeasure) throw new InvalidOperationException("measurement failure");
            return actual;
        }
        internal override void OnDisposed()
        {
            Disposed++;
            base.OnDisposed();
        }
    }
}
