using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.ImageFilters;
using SkiaSharp;

namespace AvaloniaStreamingBench;

internal static class Program
{
    public static Options Settings { get; private set; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--verify-raster-cache"))
        {
            RasterCacheValidation.Run(ReadString("--output", "artifacts/raster-cache-validation.json"));
            return;
        }
        Settings = new Options
        {
            PointsPerSeries = ReadInt("--points", 1_000),
            Seconds = ReadInt("--seconds", 15),
            Diagnostics = args.Contains("--diagnostics"),
            AnimationMs = ReadInt("--animation-ms", 100),
            UpdateMs = ReadInt("--update-ms", 16),
            Tooltip = !args.Contains("--no-tooltip"),
            Mode = ReadString("--mode", "ordinary"),
            Viewport = ReadString("--viewport", "full"),
            VisiblePoints = ReadInt("--visible-points", 10_000),
            ForceUpdate = args.Contains("--force-update"),
            Width = ReadInt("--width", 1000),
            Height = ReadInt("--height", 600),
            Scheduler = ReadString("--scheduler", "timer"),
            HighResolutionTimer = args.Contains("--high-resolution-timer"),
            Label = ReadString("--label", ""),
            StrokeWidth = double.Parse(ReadString("--stroke-width", "1"), System.Globalization.CultureInfo.InvariantCulture),
            Antialias = !args.Contains("--no-antialias"),
            Pointer = args.Contains("--pointer"),
            OnePixelStroke = args.Contains("--one-pixel-stroke"),
            RasterCache = args.Contains("--raster-cache"),
            SynchronousMeasure = args.Contains("--synchronous-measure"),
            EveryFrame = args.Contains("--every-frame"),
            ManualUpdates = args.Contains("--manual-updates"),
            TooltipShadow = ReadString("--tooltip-shadow", "default"),
            Output = ReadString("--output", "artifacts/avalonia-baseline.json")
        };
        if (Settings.Scheduler is not ("timer" or "raf") || Settings.Viewport is not ("full" or "live" or "auto") ||
            Settings.TooltipShadow is not ("default" or "on" or "off") ||
            Settings.Mode is not ("ordinary" or "streaming") || Settings.PointsPerSeries < 0 ||
            Settings.Seconds <= 0 || Settings.UpdateMs <= 0 || Settings.Width <= 0 || Settings.Height <= 0)
            throw new ArgumentException("Use mode ordinary|streaming, scheduler timer|raf, viewport full|live|auto and positive timing/dimensions.");
        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", Settings.Diagnostics);
        var builder = AppBuilder.Configure<BenchmarkApp>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        if (Settings.Diagnostics) builder.WithDeveloperTools();
        using var timerResolution = new TimerResolution(Settings.HighResolutionTimer);
        builder.StartWithClassicDesktopLifetime([]);

        string ReadString(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index < 0 ? fallback : args[index + 1];
        }
        int ReadInt(string name, int fallback) => int.Parse(ReadString(name, fallback.ToString()));
    }

    private sealed class TimerResolution : IDisposable
    {
        private readonly bool _enabled;

        public TimerResolution(bool enabled)
        {
            if (!enabled) return;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("High-resolution timer experiment requires Windows.");
            if (TimeBeginPeriod(1) != 0) throw new InvalidOperationException("Windows rejected 1 ms timer resolution.");
            _enabled = true;
        }

        public void Dispose()
        {
            if (_enabled) _ = TimeEndPeriod(1);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint period);
    }
}

internal sealed record Options
{
    public int PointsPerSeries { get; init; }
    public int Seconds { get; init; }
    public bool Diagnostics { get; init; }
    public int AnimationMs { get; init; }
    public int UpdateMs { get; init; }
    public bool Tooltip { get; init; }
    public string Mode { get; init; } = "ordinary";
    public string Viewport { get; init; } = "full";
    public int VisiblePoints { get; init; }
    public bool ForceUpdate { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Scheduler { get; init; } = "timer";
    public bool HighResolutionTimer { get; init; }
    public string Label { get; init; } = "";
    public double StrokeWidth { get; init; }
    public bool Antialias { get; init; }
    public bool Pointer { get; init; }
    public bool OnePixelStroke { get; init; }
    public bool RasterCache { get; init; }
    public bool SynchronousMeasure { get; init; }
    public bool EveryFrame { get; init; }
    public bool ManualUpdates { get; init; }
    public string TooltipShadow { get; init; } = "default";
    public string Output { get; init; } = "";
}

public sealed class BenchmarkApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var benchmark = new Benchmark(Program.Settings);
            desktop.MainWindow = benchmark.Window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class Benchmark
{
    private const int SeriesCount = 10;
    private readonly Options _options;
    private readonly CartesianChart _chart;
    private readonly List<double>[] _values;
    private readonly TimeSeriesBuffer<Measurement>[] _buffers;
    private readonly Stopwatch _clock = new();
    private readonly ConcurrentQueue<double> _drawTimes = new();
    private readonly ConcurrentQueue<Batch> _pending = new();
    private readonly ConcurrentQueue<double> _producerJitter = new();
    private readonly ConcurrentQueue<double> _renderWork = new();
    private readonly ConcurrentQueue<double> _displayedBatchAge = new();
    private readonly List<double> _measureWork = [];
    private readonly AllocationProbe _measureAllocations = new();
    private readonly AllocationProbe _drawAllocations = new();
    private readonly AllocationProbe _tooltipAllocations = new();
    private readonly AllocationProbe _updateCallAllocations = new();
    private readonly List<double> _updateCallWork = [];
    private readonly List<double> _tooltipShowWork = [];
    private Action<LvcPoint>? _movePointer;
    private int _pointerMoves;
    private long _drawStarted;
    private long _measureStarted;
    private long _latestMeasuredBatch;
    private long _lastDrawnBatch;
    private readonly ConcurrentQueue<double> _newDataDrawTimes = new();
    private readonly List<double> _callbackTimes = [];
    private readonly List<double> _tooltipTimes = [];
    private readonly List<double> _ingestTimes = [];
    private readonly List<double> _ingestLockWaitTimes = [];
    private readonly List<double> _backlogTimes = [];
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private long _producedBatches;
    private long _consumedBatches;
    private long _allocatedStart;
    private TimeSpan _cpuStart;
    private int _tooltipMatches;
    private Task _producerTask = Task.CompletedTask;
    private double _lastPublishTime;
    private bool _finishing;
    private readonly int[] _gcStart = new int[3];
    private TimeSpan _gcPauseStart;
    private readonly List<object> _resourceWindows = [];
    private double _lastResourceTime;
    private double _windowMaxIngestion;
    private double _windowMaxBacklog;

    public Benchmark(Options options)
    {
        _options = options;
        _values = options.Mode == "ordinary" ? Enumerable.Range(0, SeriesCount).Select(s =>
            Enumerable.Range(0, options.PointsPerSeries).Select(i => Value(i, s)).ToList()).ToArray() : [];
        _buffers = options.Mode == "streaming" ? Enumerable.Range(0, SeriesCount).Select(s =>
        {
            var buffer = new TimeSeriesBuffer<Measurement>(m => m.X, m => m.Y);
            for (var i = 0; i < options.PointsPerSeries; i++) buffer.Append(new Measurement(i, Value(i, s)));
            return buffer;
        }).ToArray() : [];
        if (_values.Length == 0 && _buffers.Length == 0) throw new ArgumentException("--mode must be ordinary or streaming");
        var series = Enumerable.Range(0, SeriesCount).Select(s =>
        {
            ISeries line;
            var stroke = new SolidColorPaint(new SKColor((byte)(30 + s * 20), (byte)(160 - s * 10), 210), (float)options.StrokeWidth)
            { IsAntialias = options.Antialias };
            if (options.Mode == "streaming")
                line = new StreamingLineSeries<Measurement>(_buffers[s])
                { Name = $"Signal {s}", Stroke = stroke, UseOnePixelStroke = options.OnePixelStroke, UseRasterCache = options.RasterCache };
            else
                line = new LineSeries<double>
                {
                    Values = _values[s], GeometrySize = 0, Fill = null, LineSmoothness = 0,
                    Name = $"Signal {s}", Stroke = stroke
                };
            return line;
        }).ToArray();
        _chart = new CartesianChart
        {
            Series = series,
            AnimationsSpeed = TimeSpan.FromMilliseconds(options.AnimationMs),
            UpdaterThrottler = TimeSpan.FromMilliseconds(options.UpdateMs),
            XAxes = [new Axis
            {
                MinLimit = options.Viewport == "auto" ? null : options.Viewport == "live" ? Math.Max(0, options.PointsPerSeries - options.VisiblePoints) : 0,
                MaxLimit = options.Viewport == "auto" ? null : options.Viewport == "live" ? options.PointsPerSeries : options.PointsPerSeries + options.Seconds * 1_000
            }],
            YAxes = [new Axis { MinLimit = -2, MaxLimit = 12 }]
        };
        if (options.TooltipShadow != "default")
        {
            _chart.TooltipTextPaint = new SolidColorPaint(new SKColor(30, 30, 30));
            _chart.TooltipBackgroundPaint = new SolidColorPaint(new SKColor(235, 235, 235, 230))
            {
                ImageFilter = options.TooltipShadow == "on"
                    ? new DropShadow(2, 2, 6, 6, new SKColor(0, 0, 0, 100))
                    : null
            };
        }
        Window = new Window { Width = options.Width, Height = options.Height, Title = "LiveCharts streaming benchmark (software)", Content = _chart };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(options.UpdateMs), DispatcherPriority.Background, Tick);
        _timer.Stop();
        Window.Opened += async (_, _) =>
        {
            await Task.Delay(2_000);
            if (_options.ManualUpdates) _chart.AutoUpdateEnabled = false;
            if (_options.Pointer)
            {
                _movePointer = typeof(Chart).GetMethod("InvokePointerMove", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<LvcPoint>>(_chart.CoreChart);
                if (_chart.CoreChart.Tooltip is { } tooltip)
                    _chart.Tooltip = new TooltipProbe(tooltip, ms => _tooltipShowWork.Add(ms), _tooltipAllocations);
            }
            // Zone 0 is the draw margin, zone 4 the final overlay. Paired probes bracket
            // chart geometry drawing; framework composition and presentation are excluded.
            lock (_chart.CoreChart.Canvas.Sync)
            {
                _chart.CoreChart.Canvas.AddGeometry(0, new DrawProbe(() =>
                {
                    _drawStarted = _clock.IsRunning ? Stopwatch.GetTimestamp() : 0;
                    _drawAllocations.Begin();
                })
                { Fill = new SolidColorPaint(SKColors.Black), Width = 1, Height = 1 }).ZIndex = double.MinValue;
                _chart.CoreChart.Canvas.AddGeometry(4, new DrawProbe(() =>
                {
                    if (_drawStarted == 0 || !_clock.IsRunning) return;
                    _drawAllocations.End();
                    _drawTimes.Enqueue(_clock.Elapsed.TotalMilliseconds);
                    if (_latestMeasuredBatch > _lastDrawnBatch)
                    {
                        _newDataDrawTimes.Enqueue(_clock.Elapsed.TotalMilliseconds);
                        _lastDrawnBatch = _latestMeasuredBatch;
                    }
                    _renderWork.Enqueue(Stopwatch.GetElapsedTime(_drawStarted).TotalMilliseconds);
                    _displayedBatchAge.Enqueue(Math.Max(0, _clock.Elapsed.TotalMilliseconds - _latestMeasuredBatch * 10));
                }) { Fill = new SolidColorPaint(SKColors.Black), Width = 1, Height = 1 }).ZIndex = double.MaxValue;
            }
            _chart.CoreChart.Measuring += _ =>
            {
                _measureStarted = Stopwatch.GetTimestamp();
                _measureAllocations.Begin();
            };
            _chart.CoreChart.UpdateStarted += _ =>
            {
                _measureAllocations.End();
                _measureWork.Add(Stopwatch.GetElapsedTime(_measureStarted).TotalMilliseconds);
                _latestMeasuredBatch = _consumedBatches;
            };
            _allocatedStart = GC.GetTotalAllocatedBytes();
            _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            for (var generation = 0; generation < 3; generation++) _gcStart[generation] = GC.CollectionCount(generation);
            _gcPauseStart = GC.GetTotalPauseDuration();
            _clock.Start();
            if (_options.Scheduler == "timer") _timer.Start();
            Window.RequestAnimationFrame(Frame);
            _producerTask = Task.Run(Produce);
        };
    }

    public Window Window { get; }

    private async Task Produce()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var scheduled = (long)(_clock.Elapsed.TotalMilliseconds / 10);
                while (_producedBatches < scheduled)
                {
                    var sequence = _producedBatches + 1;
                    var samples = new Measurement[SeriesCount * 10];
                    for (var s = 0; s < SeriesCount; s++)
                        for (var i = 0; i < 10; i++)
                        {
                            var index = _options.PointsPerSeries + (int)((sequence - 1) * 10) + i;
                            samples[s * 10 + i] = new Measurement(index, Value(index, s));
                        }
                    _producerJitter.Enqueue(_clock.Elapsed.TotalMilliseconds - sequence * 10);
                    _pending.Enqueue(new Batch(sequence, samples));
                    Interlocked.Exchange(ref _producedBatches, sequence);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Frame(TimeSpan time)
    {
        if (!_clock.IsRunning || _finishing) return;
        _callbackTimes.Add(_clock.Elapsed.TotalMilliseconds);
        if (_options.Scheduler == "raf" && (_options.EveryFrame || _clock.Elapsed.TotalMilliseconds - _lastPublishTime >= _options.UpdateMs))
        {
            _lastPublishTime = _clock.Elapsed.TotalMilliseconds;
            Tick(null, EventArgs.Empty);
        }
        Window.RequestAnimationFrame(Frame);
    }

    private void Tick(object? sender, EventArgs e)
    {
        var produced = Interlocked.Read(ref _producedBatches);
        var start = Stopwatch.GetTimestamp();
        if (_pending.TryPeek(out var oldest))
        {
            var backlog = Math.Max(0, _clock.Elapsed.TotalMilliseconds - oldest.Sequence * 10);
            _backlogTimes.Add(backlog);
            _windowMaxBacklog = Math.Max(_windowMaxBacklog, backlog);
        }
        var lockStarted = Stopwatch.GetTimestamp();
        double lockWait;
        lock (_chart.CoreChart.Canvas.Sync)
        {
            lockWait = Stopwatch.GetElapsedTime(lockStarted).TotalMilliseconds;
            while (_consumedBatches < produced && _pending.TryDequeue(out var batch))
            {
                for (var s = 0; s < SeriesCount; s++)
                    for (var i = 0; i < 10; i++)
                    {
                        if (_options.Mode == "streaming")
                            _buffers[s].Append(batch.Samples[s * 10 + i]);
                        else _values[s].Add(batch.Samples[s * 10 + i].Y);
                    }
                _consumedBatches++;
            }
        }
        _ingestLockWaitTimes.Add(lockWait);
        var ingestion = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _ingestTimes.Add(ingestion);
        _windowMaxIngestion = Math.Max(_windowMaxIngestion, ingestion);
        if (_options.Viewport == "live")
        {
            var count = _options.PointsPerSeries + _consumedBatches * 10;
            _chart.XAxes.First().MinLimit = Math.Max(0, count - _options.VisiblePoints);
            _chart.XAxes.First().MaxLimit = count;
        }
        if (_options.SynchronousMeasure)
        {
            start = Stopwatch.GetTimestamp();
            _updateCallAllocations.Begin();
            _chart.CoreChart.UpdateSynchronously();
            _updateCallAllocations.End();
            _updateCallWork.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        else _chart.CoreChart.Update(new ChartUpdateParams { Throttling = !_options.ForceUpdate, IsAutomaticUpdate = false });
        if (_movePointer is not null)
        {
            _movePointer(new LvcPoint((float)(_options.Width * (.5 + .3 * Math.Sin(_clock.Elapsed.TotalSeconds))), _options.Height / 2));
            _pointerMoves++;
        }
        if (_options.Tooltip)
        {
            start = Stopwatch.GetTimestamp();
            _tooltipMatches += _chart.CoreChart.FindHoveredPointsBy(new LvcPoint(_options.Width / 2, _options.Height / 2)).Count();
            _tooltipTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        if (_clock.Elapsed.TotalSeconds - _lastResourceTime >= 5) CaptureResources();
        if (_clock.Elapsed.TotalSeconds >= _options.Seconds) Finish();
    }

    private async void Finish()
    {
        _finishing = true;
        _timer.Stop();
        _stop.Cancel();
        await _producerTask;
        _clock.Stop();
        if (_clock.Elapsed.TotalSeconds - _lastResourceTime >= 1) CaptureResources();
        var draws = _drawTimes.ToArray();
        var result = new
        {
            scenario = _options.Mode + "-" + _options.Viewport, options = _options, series = SeriesCount,
            samplesPerBatchPerSeries = 10, acquisitionIntervalMs = 10,
            renderer = "Avalonia Win32 Software / Skia CPU", animationMs = _options.AnimationMs,
            measurement = "Chart geometry Draw callbacks; no physical presentation measurement. UI callbacks separately counted. Background producer generates scheduled sample batches into ConcurrentQueue; UI appends all pending batches.",
            elapsedSeconds = _clock.Elapsed.TotalSeconds,
            producedBatches = _producedBatches, consumedBatches = _consumedBatches,
            pendingBatches = _pending.Count, acquisitionJitterMs = Statistics(_producerJitter),
            retainedSamples = _options.Mode == "streaming" ? _buffers.Sum(x => (long)x.Count) : _values.Sum(x => (long)x.Count),
            displayedRepresentatives = _chart.Series.OfType<StreamingLineSeries<Measurement>>().Select(x => x.DisplayedPointCount).ToArray(),
            chartDrawCount = draws.Length, chartDrawFps = draws.Length / _clock.Elapsed.TotalSeconds,
            sourceChangedDrawCount = _newDataDrawTimes.Count,
            sourceChangedDrawFps = _newDataDrawTimes.Count / _clock.Elapsed.TotalSeconds,
            sourceChangedDrawIntervalMs = Statistics(Intervals(_newDataDrawTimes.ToArray())),
            chartMeasureCount = _measureWork.Count,
            chartWidth = _chart.Bounds.Width, chartHeight = _chart.Bounds.Height,
            renderScaling = Window.RenderScaling,
            windowVisible = Window.IsVisible,
            windowState = Window.WindowState.ToString(),
            windowActive = Window.IsActive,
            nativeWindowVisible = OperatingSystem.IsWindows() && Window.TryGetPlatformHandle() is { } visibleHandle
                ? (bool?)IsWindowVisible(visibleHandle.Handle) : null,
            nativeWindowMinimized = OperatingSystem.IsWindows() && Window.TryGetPlatformHandle() is { } iconicHandle
                ? (bool?)IsIconic(iconicHandle.Handle) : null,
            chartMeasureWorkMs = Statistics(_measureWork), chartGeometryDrawWorkMs = Statistics(_renderWork),
            synchronousUpdateCallWorkMs = Statistics(_updateCallWork),
            phaseAllocatedBytesPerCall = new
            {
                measure = Statistics(_measureAllocations.Bytes),
                draw = Statistics(_drawAllocations.Bytes),
                tooltipShow = Statistics(_tooltipAllocations.Bytes),
                synchronousUpdateCall = Statistics(_updateCallAllocations.Bytes)
            },
            latestMeasuredBatchAgeAtDrawMs = Statistics(_displayedBatchAge),
            chartDrawIntervalMs = Statistics(Intervals(draws)),
            uiCallbackFps = _callbackTimes.Count / _clock.Elapsed.TotalSeconds,
            uiCallbackIntervalMs = Statistics(Intervals(_callbackTimes)),
            tooltipQueryMs = Statistics(_tooltipTimes), tooltipMatches = _tooltipMatches,
            syntheticCorePointerMoves = _pointerMoves, tooltipShowWorkMs = Statistics(_tooltipShowWork),
            ingestionMs = Statistics(_ingestTimes), oldestPendingBatchAgeMs = Statistics(_backlogTimes),
            ingestionLockWaitMs = Statistics(_ingestLockWaitTimes),
            allocatedBytes = GC.GetTotalAllocatedBytes() - _allocatedStart,
            collections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - _gcStart[g]).ToArray(),
            gcPauseMs = (GC.GetTotalPauseDuration() - _gcPauseStart).TotalMilliseconds,
            resourceWindows = _resourceWindows,
            frameWindows = draws.Skip(1).Select((time, i) => new { time, interval = time - draws[i] })
                .GroupBy(x => (int)(x.time / 5000)).Select(g => new { startSeconds = g.Key * 5, intervalsMs = Statistics(g.Select(x => x.interval)) }).ToArray(),
            processCpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStart).TotalSeconds,
            workingSetBytes = Process.GetCurrentProcess().WorkingSet64,
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), cpuCount = Environment.ProcessorCount,
            skiaManagedVersion = typeof(SKCanvas).Assembly.GetName().Version?.ToString(),
            skiaProductVersion = FileVersionInfo.GetVersionInfo(typeof(SKCanvas).Assembly.Location).ProductVersion,
            skiaNativeVersion = SkiaSharpVersion.Native.ToString(),
            tieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.Output))!);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_options.Output, json);
        Console.WriteLine(json);
        Window.Close();
    }

    private void CaptureResources()
    {
        var now = _clock.Elapsed.TotalSeconds;
        _resourceWindows.Add(new
        {
            startSeconds = _lastResourceTime, endSeconds = now,
            retainedPerSeries = _options.Mode == "streaming" ? _buffers[0].Count : _values[0].Count,
            producedBatches = Interlocked.Read(ref _producedBatches), consumedBatches = _consumedBatches,
            allocatedBytes = GC.GetTotalAllocatedBytes() - _allocatedStart,
            collections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - _gcStart[g]).ToArray(),
            gcPauseMs = (GC.GetTotalPauseDuration() - _gcPauseStart).TotalMilliseconds,
            maxIngestionMs = _windowMaxIngestion, maxBacklogMs = _windowMaxBacklog
        });
        _lastResourceTime = now;
        _windowMaxIngestion = 0;
        _windowMaxBacklog = 0;
    }

    private static double Value(int i, int s) => Math.Sin(i * .005) + s;
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    private static IEnumerable<double> Intervals(IEnumerable<double> values) => values.Zip(values.Skip(1), (a, b) => b - a);
    private static object Statistics(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double Percentile(double p) => sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(p * (sorted.Length - 1))];
        return new { count = sorted.Length, mean = sorted.Length == 0 ? 0 : sorted.Average(), p50 = Percentile(.5), p95 = Percentile(.95), p99 = Percentile(.99), max = Percentile(1) };
    }

    private sealed class DrawProbe(Action record) : RectangleGeometry
    {
        public override void Draw(SkiaSharpDrawingContext context) => record();
    }

    private readonly record struct Measurement(double X, double Y);
    private sealed record Batch(long Sequence, Measurement[] Samples);

    private sealed class AllocationProbe
    {
        private long _start;
        private int _thread;
        public ConcurrentQueue<double> Bytes { get; } = new();

        public void Begin()
        {
            _thread = Environment.CurrentManagedThreadId;
            _start = GC.GetAllocatedBytesForCurrentThread();
        }

        public void End()
        {
            if (_thread == Environment.CurrentManagedThreadId)
                Bytes.Enqueue(GC.GetAllocatedBytesForCurrentThread() - _start);
        }
    }

    private sealed class TooltipProbe(IChartTooltip inner, Action<double> record, AllocationProbe allocations) : IChartTooltip
    {
        public void Show(IEnumerable<ChartPoint> foundPoints, Chart chart)
        {
            var started = Stopwatch.GetTimestamp();
            allocations.Begin();
            inner.Show(foundPoints, chart);
            allocations.End();
            record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        public void Hide(Chart chart) => inner.Hide(chart);
    }
}
