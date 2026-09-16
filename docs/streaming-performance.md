# Streaming measurement data: local performance experiment

## Workload and acceptance

The reference workload retains **10 million samples total across 10 series**, then appends 10 samples to each series every 10 ms (10,000 samples/second total). The desired presentation rate is 60 frames/second. History length, series count, viewport and acquisition cadence must be recorded alongside results. Ten million samples per series is a separate, larger test.

Acquisition and presentation have different responsibilities: retain every sample, but coalesce obsolete presentation requests. A 60 Hz presentation normally includes one or two acquisition batches. A fast average alone does not establish smooth presentation: inspect frame intervals, p95/p99 work duration, missed frame budgets, backlog, memory, and pointer-query latency.

## Measurement layers

1. **Retained CPU/Skia benchmark:** synchronous chart measurement and software rasterization on a reusable surface. Separates ingestion, measurement and drawing. It measures work cost, not Avalonia FPS.
2. **Avalonia desktop benchmark:** software rendering, a real window and event loop, continuous acquisition and pointer lookup. Chart draw callbacks and UI frame callbacks are recorded separately. Neither callback alone proves physical monitor presentation.
3. **Correctness:** reduction must preserve chronological order, extrema, viewport boundary segments, missing-data breaks and original sample identity. Index queries must agree with a brute-force reference.

Do not compare the existing image-export helper directly with retained update results: `DrawOnCanvas` resets first-draw state and unloads the chart. Those are useful export measurements, but do not reproduce a retained desktop chart.

## Planned controlled experiments

- Ordinary line defaults versus markers, fill, smoothing and point animations disabled.
- Full raw history versus a viewport representation fed through an ordinary line series.
- Full-scan reduction versus indexed range summaries.
- Ordinary per-point geometry versus one packed path per series.
- Per-acquisition updates versus presentation-rate updates.
- Full-history overview versus a moving live window, with pointer lookup and axis changes.

Keep raw results with each experiment's configuration and limitations. Proposed experiments are not measured improvements.

## Data ownership

An optimized append-only source requires sorted timestamps and explicit batch appends. Mapping is evaluated when samples enter the source. Mutating an original object or changing a calibration captured by a mapper cannot silently update cached numeric coordinates; rebuild the source when those change. Preserve the source model for lazy tooltip formatting.

The initial experimental source is caller-synchronized. Publish acquisition batches to the UI owner; do not mutate it concurrently with chart measurement, drawing or hit-testing. Keep the raw source independent from the viewport representation. Reducing the display must never discard acquired samples.

## Experimental API

```csharp
var history = new TimeSeriesBuffer<Measurement>(m => m.Timestamp, m => m.Value);
var series = new StreamingLineSeries<Measurement>(history)
{
    Name = "Pressure",
    Stroke = new SolidColorPaint(SKColors.DodgerBlue, 1),
    YToolTipLabelFormatter = point => $"{point.Model.Value:0.000} bar",
    // Optional for stable viewports; keeps a cropped CPU raster per chart view.
    // UseRasterCache = true,
    // UseOnePixelStroke = true,
};
chart.Series = new[] { series };

// On the UI owner, publish all acquisition batches since the last presentation:
lock (chart.CoreChart.Canvas.Sync)
{
    history.AppendRange(batch);
}
chart.CoreChart.Update();
```

Use `LiveChartsCore.Defaults`, `LiveChartsCore.SkiaSharpView`,
`LiveChartsCore.SkiaSharpView.Painting` and `SkiaSharp`. Timestamp is a finite numeric X
coordinate in consistent units; irregular spacing and duplicate timestamps are allowed.
The source preserves the original model and index for tooltips. `double.NaN` Y marks a gap.
If mapping or validation fails partway through a batch, already accepted samples remain.

The new series uses the existing engine render-override hook. Its source supplies exact
history bounds and indexed visible bounds; its drawing path consumes at most five selected
indices per horizontal bucket plus two boundary neighbors. Four representatives preserve
entry, extrema and exit; a fifth can identify a gap. This is a bound on representative count,
not a claim that a line paints only one physical pixel per column. The current bucket width
uses LiveCharts draw-margin coordinates. Dense subpixel oscillations and multiple subpixel
gaps are reduced, but selected extrema are actual samples and lines never bridge a detected
gap. This is not a promise of pixel-identical antialiased output to the unreduced series.

This opt-in prototype supports straight lines on linear Cartesian axes and nearest-X
tooltips. It does not implement fills, markers, smoothing, stacking, data labels, arbitrary
historical edits or automatic retention. `UseRasterCache` is intended for stable, append-only
viewports; it is bypassed while axes animate and for paints with unsupported blend/filter
effects. The inherited `Values` and `Mapping` properties
are superseded by `Source`; configure conversion on the source. Unsupported inherited
styling features are not applied. This API surface needs refinement before upstreaming.

Keep `chart`, series, source and paints stable. `Clear()` releases history and its index;
it also prevents a tooltip from returning replacement models against a stale rendered
frame until the next measurement. It does not notify the chart automatically.

When an application already publishes at presentation cadence, compare the default
throttling with `chart.CoreChart.Update(new ChartUpdateParams
{ IsAutomaticUpdate = false, Throttling = false })`. This removes a second throttle, but
still uses the framework's update scheduling; it is not a synchronous completed-render API.
Do not force updates once per sample.

When the application owns a UI frame callback, it can instead call the supported
`chart.CoreChart.UpdateSynchronously()` API once after consuming the pending batches.
It invokes the update immediately on that same thread under `Canvas.Sync`; the framework
still renders through its normal render loop. Existing loaded/active-render checks remain:
Cartesian measurement is skipped if no new draw has started since its last measurement.
Thus an immediate call is not a guarantee of fresh measurement or a presented frame.
When measurement does run, it finishes before the method returns. Call only on the chart's UI thread (or the
owning thread of an in-memory chart), and supply the presentation cadence yourself.
The default is a manual update, so it works with `AutoUpdateEnabled = false`.
Pass `isAutomaticUpdate: true` when the call should respect that setting. Measurement
exceptions propagate to the caller. Requests made recursively during measurement are
coalesced into a later scheduled update.

Choose one scheduling strategy: synchronous updates do not cancel older throttled or
queued updates. The existing `Update(...)` API keeps its asynchronous scheduling.
Short diagnostic runs showed that avoiding the UI-to-worker-to-UI scheduling round trip
can improve cadence, but sustained performance must still be measured for the chosen
data, viewport, pointer load and machine. Synchronous measurement alone is not a 60 FPS
guarantee.

For a moving viewport, set `chart.AutoUpdateEnabled = false` when choosing manual
frame publication. Axis limit setters otherwise request their own automatic updates,
which can leave extra queued measurements alongside the synchronous call. Update the
source and both axis limits together under the canvas lock, then measure once:

```csharp
// Set once when the application takes ownership of publication.
chart.AutoUpdateEnabled = false;

// Inside the application's UI frame callback:
lock (chart.CoreChart.Canvas.Sync)
{
    history.AppendRange(batch);
    xAxis.MinLimit = newestTimestamp - visibleDuration;
    xAxis.MaxLimit = newestTimestamp;
    chart.CoreChart.UpdateSynchronously();
}
```

The same lock must cover source mutation because the framework may draw on another
thread. With automatic updates disabled, the application must publish changes to axes,
styles and other chart properties too; its frame callback supplies that measurement.

For dense software-rendered paths, compare `UseOnePixelStroke = true`. It keeps the
paint's color and antialiasing but replaces its thickness with one physical device pixel.
At 125% display scaling this is 0.8 logical units. Controlled Skia benchmarks found a
large cost difference between this positive one-pixel stroke and a fractional physical
width; zero-width hairlines take a different rasterization path and were slower here.
This is an explicit appearance choice, not a general promise about all hardware or data.

Fixed viewport limits reuse the selected history and update only buckets affected by
appends. Panning, resizing, changing limits or clearing the source rebuilds that viewport's
selection. A moving live window still queries the index on every measurement, with work
bounded by viewport width and the index rather than scanning the complete history.

## What changes for the application

| Concern | Experimental streaming API |
| --- | --- |
| Custom measurement models | Supply X/Y selectors once; original models remain available to formatters. |
| New data | Call `Append` or `AppendRange`, then publish one chart update per presentation interval. |
| Threading | Transfer acquisition batches to the UI owner; synchronize all source access with chart work. |
| Edits and recalibration | Rebuild the source; changing a retained model does not update its cached coordinates. |
| Missing data | Append a sample with `double.NaN` Y. The selected path checks for intervening gaps. |
| Fixed viewport | Selection and optional raster caches reuse historical work and refresh the changing tail. |
| Moving viewport | Selection is recomputed from the index; changing the pixel mapping invalidates cached raster pixels. |
| Animation | Linear axis pan/zoom follows the chart's motion scalers. Individual samples do not morph. |
| Retention | All raw samples remain retained until `Clear`; automatic eviction is not implemented. |

The generic source adds cached numeric coordinates and an index alongside the model.
Smaller index leaf blocks improved selection speed in the measured workload but increased
memory. The optional raster cache adds cropped pixel surfaces for each series and chart
view. Measure memory for your acquisition duration and channel count when deciding
whether these tradeoffs fit your application.

## Reproduce the sustained target workload

The fixed-overview configuration reached **61.25 newly measured chart draws/s** in one
75-second trial and **58.55** in the final clean repeat. Repeatable strict 60 FPS is not
established; this is the accepted stopping point for the current iteration.
Moving/automatically expanding viewports and
default tooltip shadows have not met the same target. This measures chart drawing,
not physical monitor presents.

```powershell
./tests/AvaloniaStreamingBench/Run.ps1 -Mode streaming -PointsPerSeries 1000000 `
    -Seconds 75 -Viewport full -Scheduler raf -AnimationMs 100 `
    -SynchronousMeasure -EveryFrame -ManualUpdates -HighResolutionTimer `
    -OnePixelStroke -RasterCache -Pointer -TooltipShadow off `
    -Output artifacts/avalonia-reproduced-75.json
```

Add `-Restore` for the first build if dependencies are not restored. The runner retains
ten sources of one million samples and appends ten samples to each every 10 ms. Its fixed
X range reserves space for the trial's incoming samples; Y limits stay fixed. The tested
window is 1000×600 logical pixels at 125% Windows display scaling. The Windows high-resolution
timer request is process-scoped; it is part of the tested harness configuration.

To use the same tooltip appearance in an application, set both colors explicitly so an
inherited dark-theme foreground does not become unreadable on a light background:

```csharp
chart.TooltipBackgroundPaint = new SolidColorPaint(new SKColor(235, 235, 235, 230))
{
    ImageFilter = null
};
chart.TooltipTextPaint = new SolidColorPaint(new SKColor(30, 30, 30));
```

This removes only the tooltip shadow effect; the tooltip still formats original models,
lays out all series, follows pointer movement and animates. `UseOnePixelStroke` is another
explicit appearance choice. Keep these settings distinct from unmodified default styling
when reporting or comparing performance.
