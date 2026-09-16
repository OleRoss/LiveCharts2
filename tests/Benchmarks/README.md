# Running benchmarks

Build Release once with the repository's sandbox-safe .NET environment (see root
AGENTS.md), then invoke the resulting `Benchmarks.dll` directly. A direct run avoids
including build time and source-generation work in the measurement.

```powershell
./tests/Benchmarks/build.ps1 -Restore  # first run; requires NuGet access
./tests/Benchmarks/build.ps1           # subsequent builds
```

## Streaming CPU diagnostic

```powershell
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll stream 1000 30 artifacts/stream-1000-default.json default
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll stream 1000 30 artifacts/stream-1000-stripped.json stripped
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll stream 10000 30 artifacts/stream-10000-stripped.json stripped
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll stream 1000000 60 artifacts/stream-1m-latest.json streaming latest
```

Arguments: points **per series**, measured iterations, JSON output, and mode
(`default`, `stripped`, `indexed-line`, or `streaming`), then optional viewport
(`full` by default, or `latest` for the most recent 10,000 samples). There are always ten series, and every iteration appends
ten samples to each series. Three iterations warm the retained chart before timing.
Exit code 2 means p95 exceeded the 16.67 ms budget; 0 means it passed.

The retained chart calls the same synchronous `Measure` used by UI updates and draws
into a reused CPU Skia surface. The frame total includes append, measure, draw, and
one tooltip hit test. JSON preserves every timing, percentiles, allocations, GC
counts, process memory and first render. `stripped` disables markers, area fill and
curve smoothing. `indexed-line` feeds indexed viewport representatives into an
ordinary line series; `streaming` uses the experimental renderer over the same
indexed source. Both use original `(X,Y)` tuples, and source mapping runs at append.
Every mode disables animation. Indexed modes separately report initial source build
time; selection into ordinary lines is included in frame time.

This diagnoses CPU cost; it does **not** establish Avalonia FPS. It does not run a UI
dispatcher/compositor or pace acquisition at 10 ms. Separate actual Avalonia runs
must verify presented frames, producer timing, animation and input responsiveness.
Run cases one at a time with no concurrent builds or benchmarks. Start small:
ordinary per-point geometry at millions of points may exhaust memory.

## BenchmarkDotNet suites

Source-only index microbenchmarks are also available:

```powershell
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll index 1000000 300 artifacts/index-1m.json
```

These separately time construction, viewport selection, exact bounds, nearest raw
sample lookup, and ten-sample appends. They use one source and do not measure FPS.

## Isolated CPU raster experiment

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll raster 1250 700 120 artifacts/raster-1250-run1.json
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll raster 1000 600 60 artifacts/raster-dpi125.json 1.25 0.8 path
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll raster 1000 600 120 artifacts/raster-envelope.json 1.25 1 envelope
```

This precomputes identical indexed geometry for ten million raw measurements across
ten series. It compares paths with independent line segments, antialiasing on/off,
and stroke widths zero (hairline), one/two. Optional arguments after the JSON path
are canvas scale, comma-separated stroke widths, and comma-separated algorithms.
Scaling the canvas also changes physical stroke width: increasing the surface size
alone does not reproduce high-DPI UI behavior. Every case warms for at least two seconds and exports raw
timings and a PNG. The timed operation includes clearing and drawing the CPU surface.
It excludes index queries, chart measurement, axes, input and UI presentation.
Independent segments change joins; disabling antialiasing produces jagged edges.
The optional `envelope` experiment fills each column's exact minimum-to-maximum
range. It changes the visualization: within-column order and gaps are lost, and
stroke width does not affect the filled rectangle. It is not a production renderer.

## BenchmarkDotNet commands

```powershell
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll --list flat
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll --filter '*LineSeriesBench*' --artifacts artifacts/bdn
dotnet artifacts/benchmark-baseline-out/Benchmarks.dll compare artifacts/base artifacts/head artifacts/comparison.md
```

Existing series benchmarks use image export (`DrawOnCanvas`). That path resets
first-draw state and unloads the chart after rendering. They measure repeated
exports, **not retained UI updates**, even where historical method names say
`Reinvalidate` or `UpdateOnePoint`. Use `stream` for retained update measurements.
