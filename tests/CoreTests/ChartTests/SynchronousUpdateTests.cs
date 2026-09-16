using System;
using System.Collections.Generic;
using System.Threading;
using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Motion;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.SKCharts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreTests.ChartTests;

[TestClass]
public class SynchronousUpdateTests
{
    [TestMethod]
    [DoNotParallelize]
    public void ProductionRenderingGateSkipsRepeatedCallsUntilAnotherDrawStarts()
    {
        var wasTesting = CoreMotionCanvas.IsTesting;
        var chart = new SKCartesianChart
        {
            Series = [new LineSeries<double> { Values = [1, 2, 3] }]
        };
        var measured = 0;
        chart.CoreChart.Measuring += _ => measured++;
        try
        {
            CoreMotionCanvas.IsTesting = false;
            chart.CoreChart.IsLoaded = true;
            chart.CoreCanvas._lastFrameTimestamp = 100;
            chart.CoreChart.UpdateSynchronously();
            chart.CoreChart.UpdateSynchronously();
            Assert.AreEqual(1, measured, "Synchronous updates retain the active-render guard.");

            // DrawFrame advances this marker on entry, before taking Canvas.Sync.
            chart.CoreCanvas._lastFrameTimestamp = 101;
            chart.CoreChart.UpdateSynchronously();
            Assert.AreEqual(2, measured);

            chart.CoreChart.Unload();
            chart.CoreCanvas._lastFrameTimestamp = 102;
            chart.CoreChart.UpdateSynchronously();
            Assert.AreEqual(2, measured, "Unloaded charts still skip measurement.");
        }
        finally
        {
            chart.CoreChart.Unload();
            CoreMotionCanvas.IsTesting = wasTesting;
        }
    }

    [TestMethod]
    public void MeasuresOnCallingThreadUnderCanvasLockBeforeReturning()
    {
        var chart = new RecordingChart();
        var caller = Environment.CurrentManagedThreadId;
        var completed = false;
        chart.OnMeasure = () =>
        {
            Assert.AreEqual(caller, Environment.CurrentManagedThreadId);
            Assert.IsTrue(Monitor.IsEntered(chart.Canvas.Sync));
            completed = true;
        };

        chart.UpdateSynchronously();

        Assert.IsTrue(completed);
        Assert.AreEqual(1, chart.Measures);
        Assert.AreEqual(0, chart.ScheduledUpdates);
    }

    [TestMethod]
    public void ManualUpdateIgnoresAutoUpdateDisabledButAutomaticUpdateRespectsIt()
    {
        var chart = new RecordingChart();
        chart.View.AutoUpdateEnabled = false;
        chart.UpdateSynchronously(isAutomaticUpdate: true);
        Assert.AreEqual(0, chart.Measures);
        chart.UpdateSynchronously();
        Assert.AreEqual(1, chart.Measures);
        chart.View.AutoUpdateEnabled = true;
        chart.UpdateSynchronously(isAutomaticUpdate: true);
        Assert.AreEqual(2, chart.Measures);
    }

    [TestMethod]
    public void NestedCallsCoalesceIntoOneScheduledUpdateWithoutRecursion()
    {
        var chart = new RecordingChart();
        chart.View.AutoUpdateEnabled = false;
        chart.OnMeasure = () =>
        {
            chart.UpdateSynchronously();
            chart.UpdateSynchronously();
            Assert.AreEqual(1, chart.Measures);
            Assert.AreEqual(0, chart.ScheduledUpdates);
        };

        chart.UpdateSynchronously();

        Assert.AreEqual(1, chart.Measures);
        Assert.AreEqual(1, chart.ScheduledUpdates);
        Assert.IsFalse(chart.LastScheduledUpdate!.IsAutomaticUpdate);
        Assert.IsTrue(chart.LastScheduledUpdate.Throttling);
        chart.OnMeasure = null;
        chart.UpdateSynchronously();
        Assert.AreEqual(2, chart.Measures);
    }

    [TestMethod]
    public void MeasurementFailurePropagatesAndReleasesGuardAndLock()
    {
        var chart = new RecordingChart();
        var expected = new InvalidOperationException("measurement failure");
        chart.OnMeasure = () => throw expected;
        try
        {
            chart.UpdateSynchronously();
            Assert.Fail("The synchronous caller must receive measurement failures.");
        }
        catch (InvalidOperationException actual)
        {
            Assert.AreSame(expected, actual);
        }

        Assert.IsFalse(Monitor.IsEntered(chart.Canvas.Sync));
        chart.OnMeasure = null;
        chart.UpdateSynchronously();
        Assert.AreEqual(2, chart.Measures);
        Assert.AreEqual(0, chart.ScheduledUpdates);
    }

    [TestMethod]
    public void ApplyThemeGuardsNestedSynchronousUpdates()
    {
        var chart = new RecordingChart();
        chart.OnMeasure = () =>
        {
            if (chart.Measures == 1) chart.UpdateSynchronously();
        };

        chart.ApplyTheme();

        Assert.AreEqual(1, chart.Measures, "Applying a theme must use the same reentrancy guard.");
        Assert.AreEqual(1, chart.ScheduledUpdates);
    }

    [TestMethod]
    public void InMemoryExportGuardsNestedSynchronousUpdates()
    {
        var chart = new RecordingChart();
        chart.OnMeasure = () =>
        {
            if (chart.Measures == 1) chart.UpdateSynchronously();
        };
        var export = new RecordingExport(chart);

        using var image = export.GetImage();

        Assert.AreEqual(1, chart.Measures, "Export must not reenter measurement from its callback.");
        Assert.AreEqual(1, chart.ScheduledUpdates);
    }

    private sealed class RecordingExport(Chart chart) : InMemorySkiaSharpChart
    {
        protected override Chart GetCoreChart() => chart;
    }

    private sealed class RecordingChart : Chart
    {
        public RecordingChart() : this(new SKCartesianChart()) { }

        private RecordingChart(SKCartesianChart view) : base(view.CoreCanvas, view, ChartKind.Cartesian)
        {
            View = view;
        }

        public Action? OnMeasure { get; set; }
        public int Measures { get; private set; }
        public int ScheduledUpdates { get; private set; }
        public ChartUpdateParams? LastScheduledUpdate { get; private set; }
        public override IChartView View { get; }
        public override IEnumerable<ISeries> VisibleSeries => [];
        public override IEnumerable<ISeries> Series => [];
        public override IEnumerable<ChartPoint> FindHoveredPointsBy(LvcPoint pointerPosition) => [];

        public override void Update(ChartUpdateParams? chartUpdateParams = null)
        {
            ScheduledUpdates++;
            LastScheduledUpdate = chartUpdateParams;
        }

        protected internal override void Measure()
        {
            Measures++;
            OnMeasure?.Invoke();
        }
    }
}
