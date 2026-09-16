# Avalonia streaming measurement

This executable opens a 1000 × 600 Avalonia chart using **Win32 software rendering**.
Ten lines receive ten values per series per scheduled 10 ms batch. The UI consumes
all pending batches every nominal 16 ms; overload therefore appears as backlog,
slower frames, and an extended measurement interval rather than silently dropped data.
`--points` means initial points **per series**, so `--points 1000000` retains ten
million points across the chart before streaming starts.

`--viewport full` fixes X limits from zero through the last sample expected at the
end of the requested run, reserving horizontal space for incoming data. This stable
viewport enables append-tail selection and raster reuse. `--viewport auto` leaves X
limits automatic and expands the overview as data arrives. `--viewport live` moves
the last 10,000 samples across the chart. Results from the fixed viewport must not
be generalized to moving or automatically expanding limits.

Build Release with the repository's sandbox-safe .NET environment, then run:

```powershell
artifacts\avalonia-bin\AvaloniaStreamingBench.exe --points 1000 --seconds 15 --output artifacts\avalonia-baseline.json
artifacts\avalonia-bin\AvaloniaStreamingBench.exe --mode streaming --points 1000000 --seconds 15 --viewport full --output artifacts\avalonia-streaming-10m.json
artifacts\avalonia-bin\AvaloniaStreamingBench.exe --mode streaming --points 1000000 --seconds 15 --viewport live --visible-points 10000 --output artifacts\avalonia-streaming-live-10m.json
```

There is a two-second chart warmup before acquisition and measurement begin. The
chart remains visible until the measured interval finishes, then exits. Run one
measurement at a time, with other benchmark processes and builds stopped.

`--animation-ms` defaults to 100 and `--update-ms` defaults to 16. `--force-update`
bypasses the chart's second throttle after the UI timer has already throttled updates.
`--scheduler raf` publishes from Avalonia animation-frame callbacks instead of a timer.
Use `--every-frame` with RAF to publish on each available frame callback; a 16 ms
elapsed-time gate can skip callbacks because compositor intervals fluctuate. This
keeps the chart's internal throttle at `--update-ms` (default 16). The earlier
`public-api-every-raf` artifact used `--update-ms 1`, changing both settings.
`--synchronous-measure` calls `CoreChart.UpdateSynchronously()` on the UI publisher,
avoiding the ordinary update's worker-to-UI dispatch. Earlier artifacts labeled
`coordinate-sync-diagnostic` and `coordinate-sync-growth-diagnostic` used a reflected
`Measure` delegate under the canvas lock; keep them distinct from public API runs.
`--manual-updates` disables `AutoUpdateEnabled` after initial layout, leaving the
publisher in charge of updates. This avoids redundant automatic updates when the
live viewport changes axis limits.
`--high-resolution-timer` scopes a Windows `timeBeginPeriod(1)`/`timeEndPeriod(1)`
request to the benchmark process lifetime to test acquisition timer jitter. This is
a harness experiment and is not a library requirement.
`--stroke-width 0` tests Skia hairlines; `--no-antialias` tests rasterization without
edge smoothing. Both change visual quality and should be reported explicitly.
`--one-pixel-stroke` opts into the experimental series' DPI-aware, one-device-pixel
stroke. `--raster-cache` opts into its CPU raster cache for a stable viewport and
append-only history. Record both flags when comparing results.
`--pointer` exercises the real tooltip show/format/layout path with synthetic core
pointer moves (a cached delegate to the engine method), while recording `Show` work.
`--tooltip-shadow on|off` explicitly supplies the same light background and dark
text in both arms, changing only its drop-shadow filter. Compare these two values together;
the `default` setting leaves theme initialization untouched and is a separate control.
Disabling the shadow changes visual appearance and must be reported as a tradeoff.
For render timeline diagnostics, apply both patches in `tests/AvaloniaStreamingBench/diagnostics/` named
`frame-trace-motioncanvas.patch` and `frame-trace-harness.patch`, rebuild, then use
`--frame-trace`. They record control render, queued continuation, custom draw,
geometry, measure, and RAF events with thread and batch IDs. Traced runs are
diagnostic and separate from acceptance timings. The shipping code has no trace hook.
Window visibility, minimized state, and native Win32 visibility are recorded alongside
each result; draw callbacks still do not prove physical monitor presentation.
It does not simulate OS input dispatch. Use the default query-only mode for isolated
lookup benchmarks, and this mode for tooltip lifecycle/rendering measurements.
`--no-tooltip`
isolates drawing from hover lookup. `--diagnostics` explicitly enables Avalonia MCP
Developer Tools for inspection; omit it from all performance runs. The ordinary mode
uses `List<double>` and `LineSeries<double>`. Streaming mode retains `Measurement`
value models in `TimeSeriesBuffer<Measurement>` with cached X/Y selectors and uses
`StreamingLineSeries<Measurement>`. Both generate the identical X/Y signal.

## What the numbers mean

- **chartDrawFps:** A no-op geometry on the LiveCharts canvas records each actual
  chart drawing pass through Skia. It does not force invalidation or draw extra frames.
- **uiCallbackFps:** Avalonia `RequestAnimationFrame` callbacks. These can continue
  when the chart is unchanged and are not evidence that chart data was rendered.
- **Intervals:** Nearest-rank p50/p95/p99/max wall-clock intervals, reported separately
  for chart drawing passes and UI callbacks.
- **chartMeasureWorkMs:** Engine `Measuring` through `UpdateStarted` events bracket the
  chart measure pass. `UpdateStarted` fires at the end of measuring, before invalidation.
- **chartGeometryDrawWorkMs:** Two no-op geometry probes bracket drawing from the first
  draw-margin task to the last overlay task. This excludes Avalonia composition and
  presentation and only approximates the whole LiveCharts drawing pass.
- **latestMeasuredBatchAgeAtDrawMs:** Age of the newest batch included in the latest
  completed chart measure, sampled after drawing. It measures chart processing
  freshness, not physical presentation latency or completion of visual transitions.
- **tooltipQueryMs:** Fully enumerates the real chart engine's hover lookup at the
  chart center on every ingest tick. This measures lookup, not popup layout or pointer
  event-to-display latency.
- **oldestPendingBatchAgeMs:** Age since the oldest queued batch's scheduled deadline,
  sampled before each UI ingestion. This includes the intentional presentation throttle.
- **acquisitionJitterMs:** Background sample production time minus the scheduled batch
  deadline. Late timer wakeups catch up all scheduled batches; no samples are skipped.
- **ingestionMs:** Appending all pending batches, including canvas-lock wait. Chart
  measurement and drawing are excluded, including in synchronous-update runs.
- CPU time, allocations, retained sample count, acquisition/consumption counts, and
  working set are recorded for context.

The producer generates batches of 100 deterministic samples using a background 10 ms
periodic timer and passes them through `ConcurrentQueue` to the UI thread.
This simulates a lossless acquisition schedule, not hardware or network I/O. Values
remain in append-only storage throughout the run. It does not test eviction.

`sourceChangedDrawFps` counts only draws whose measured consumed-batch sequence
advanced since the previous draw. It separates newly measured samples from repeated
animation or tooltip draws. `chartMeasureCount` counts completed measure passes.
Phase allocation probes use current-thread allocated-byte deltas and discard
cross-thread pairs. The whole synchronous update call includes its measure phase,
so these allocation figures overlap and must not be added together. They exclude
native Skia memory and work on other threads.
The publisher locks `CoreChart.Canvas.Sync` while appending queued samples because
render-thread viewport refinement can read the retained source. `ingestionLockWaitMs`
reports time waiting for that lock; `ingestionMs` includes it. Earlier artifacts
without this field used UI-thread appends without the render lock and are exploratory.

**No physical presentation FPS is measured.** Draw callbacks establish that CPU chart
rendering occurred, not that Windows composed or a monitor displayed every result.
Window occlusion, desktop session scheduling, and display refresh can affect results.
For a physical 60 FPS acceptance test, supplement these statistics with presentation
tracing and a visible, unoccluded window on the target deployment machine.

## Matched comparisons

For repeated matched comparisons, `Compare.ps1` runs preserved baseline and candidate
executables in A/B/B/A order with identical full-viewport, pointer, manual-update
settings. Build and preserve the two output directories before running it. The script
keeps the caller's tiered-compilation environment unchanged across all four runs and
writes separate JSON and stdout/stderr files for each process.

## Raster cache verification

`--verify-raster-cache --output artifacts/raster-cache-validation.json` runs pixel
comparisons against direct Skia path drawing without opening Avalonia. It checks
append/tail edits, DPI and viewport changes, mutable/replacement paints, translucent
edges, gaps, fallback restoration, and disposal. The tolerance is four channel levels
out of 255, based on comparisons of cropped and origin-zero surfaces that both
exhibited this intermediate antialias/premultiplied-pixel rounding. Every case records
its actual maximum and mean error. Failing cases export images.
