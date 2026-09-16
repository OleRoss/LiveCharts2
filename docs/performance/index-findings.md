# Indexed streaming source findings

## Contract and development experience

`TimeSeriesBuffer<T>` retains original measurement models and maps finite, nondecreasing X
and finite-or-NaN Y once on append. Duplicate timestamps are supported. Original model mutations
do not change cached coordinates. Appends and chart queries must be serialized by the caller,
normally by publishing batches on the UI thread. `AppendRange` retains the valid prefix if a
later item fails validation or mapping. `Clear` resets history and the renderer's internal epoch.

The source keeps X, Y and models in 4,096-element chunks, avoiding full-history array copying
when appending. Completed blocks of eight samples form an append-only binary extrema hierarchy
(the initial experiment used 32).
There is no arbitrary edit, retention window or automatic property-change subscription yet.

`Select` returns original indices for first, minimum, maximum, last and first NaN per equal-width
linear-X bucket, plus neighboring samples outside the viewport. The upper bound is
`5 * pixelWidth + 2`, rather than an exact one-vertex-per-pixel rule. Extrema, chronology and
original identities are retained; subpixel oscillations and multiple gaps are reduced. The
renderer checks `HasGapBetween` so omitted NaNs never create a false connecting line. Multiple
finite runs inside one bucket can consequently disappear; this is conservative gap fidelity.

Full-history bounds are constant-time. Viewport bounds are exact for samples within the
inclusive X interval. Nearest-X tooltip lookup binary-searches raw samples independently of
the displayed representatives, choosing the first duplicate for exact timestamp matches.
Equal-distance ties choose the lower timestamp. A nearest gap produces no tooltip.

## Hypotheses tested

### 1. Index once, select representatives without scanning all history

The initial implementation stores the minimum/maximum original indices and first gap in each
summary. A query merges complete blocks and scans at most 62 boundary samples per bucket.
X bounds use binary search. Append work is amortized constant time, with logarithmic cascades
when a newly completed block completes larger nodes.

The initial `artifacts/index-{100k,1m,10m}.json` runs established that raw history was no longer
enumerated per query, but full-history selection was still considerably slower than selecting
the latest 10,000 measurements. This motivated locality experiments rather than claiming the
source alone achieved the frame-rate goal.

### 2. Cache extrema values in hierarchy nodes

The initial summaries dereferenced historic Y arrays whenever two extrema indices were
compared. Storing `MinY` and `MaxY` with each summary removes these random historical reads.
The summary grows from 12 to 32 bytes. This increases index memory but preserves the public
API and exact selection behavior.

Controlled paired runs used `DOTNET_TieredCompilation=0`, 3,000 iterations per operation,
three fresh processes per variant and size, alternating old and new binaries. No other agent
benchmark or build ran concurrently. Operating-system scheduling and machine power/frequency
still cause visible variation. The table reports median **per-process mean** query times,
not a pooled percentile or a UI frame-rate measurement.

| One-series history | Initial mean selection | Cached-Y mean selection | Improvement | Initial build median | Cached-Y build median |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1,000,000 | 1.958 ms | 1.288 ms | 34% | 169.8 ms | 52.1 ms |
| 10,000,000 | 4.590 ms | 2.940 ms | 36% | 1,510.9 ms | 789.1 ms |

At 10M, the three initial full-selection p95 values span 4.56–8.27 ms; cached-Y p95 values
span 2.66–4.97 ms. Every paired full-selection run improved (28–38% at 10M).
Latest-10K selection did not show a consistent improvement, so cache-Y should not be described
as a universal query speedup. The source benchmark uses a `double` original model and retains
both X and mapped Y: managed retained memory at 10M increases from 241.1 to 261.2 MiB.
Model graphs retained by reference-type application models are additional memory.

Raw evidence: `artifacts/index-1000000-{v1,cached-y}-stable-{1,2,3}.json` and
`artifacts/index-10000000-{v1,cached-y}-stable-{1,2,3}.json`.
The original executable is preserved in `artifacts/benchmark-v1-out`.

### 3. Replace runtime division and modulo in block decomposition

All hierarchy node spans are powers of two. The query now tests alignment with
`(block & ((size << 1) - 1)) == 0` and indexes nodes with `block >> level`, replacing
runtime modulo and division without changing node choice or fidelity.

The first controlled 10M run (3,000 iterations, tiering disabled) gives full-history selection
mean **1.273 ms**, p50 **1.123 ms**, p95 **2.044 ms**, p99 **2.331 ms**. Latest-10K mean
is **0.349 ms**. Memory is unchanged from cached-Y. Two subsequent runs produced means
**2.45 ms** and **2.93 ms**, demonstrating substantial machine variability. The median of
these three means is 2.45 ms, versus 2.94 ms for cached-Y; the isolated first-run improvement
must not be presented as the established effect size. Evidence:
`artifacts/index-10000000-bitmask-stable-{1,2,3}.json`.

### 4. Reduce leaf blocks from 32 samples to eight

This reduces the maximum raw boundary scan from 62 to 14 samples per bucket, at the cost
of four times as many hierarchy leaves. Both variants retain cached Y and bitwise node
decomposition. Three paired fresh-process runs per history size alternate only the compiled
core assembly, use the same benchmark executable and disable tiered compilation.

| One-series history | Block32 selection mean median | Block8 selection mean median | Improvement | Block32 retained | Block8 retained |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1,000,000 | 1.077 ms | 0.613 ms | 43% | 25.0 MiB | 31.0 MiB |
| 10,000,000 | 1.481 ms | 1.024 ms | 31% | 261.2 MiB | 357.2 MiB |

Every paired full-selection run improved. At 1M, Block8 means span 0.550–0.755 ms;
at 10M, they span 0.985–1.126 ms. Latest-10K mean medians improve from 0.384 to
0.321 ms at 1M and from 0.403 to 0.329 ms at 10M. Source build medians are essentially
unchanged at 1M (59.7 versus 59.4 ms); at 10M they rise from 590.2 to 658.3 ms.
Block8 is retained in the experimental implementation. The larger relative memory increase
at 10M reflects the power-of-two capacities of summary lists; source model/X/Y chunks
themselves do not resize. Raw evidence:
`artifacts/index-{1000000,10000000}-{block32,block8}-paired-{1,2,3}.json`.

### 5. Enable exact suffix selection for a renderer-owned viewport cache

The internal `SelectFromBucket` helper uses the same full-viewport bucket boundaries as
`Select`, starts directly at a specified dirty bucket and includes its preceding raw neighbor.
A renderer can retain cached indices strictly before that neighbor and append the refreshed
suffix. Work depends on the number of dirty buckets rather than traversing every bucket.
This adds no public API. A randomized regression verifies that joining each possible suffix
to the matching prefix reproduces full selection across appends, gaps and duplicate X values.
Renderer cache invalidation, integration and end-to-end cache benchmarks are separate work.

### 6. Isolate per-point native path calls versus bulk path construction

Local inspection of SkiaSharp 2.88.9 confirms that `SKPath.AddPoly(SKPoint[], false)` pins
the array and submits all points in one native call. It accepts the complete array length;
the span overload is not available in that supported version.

`PathBuildBenchmark` compares ten 4,000-point paths using precomputed pixel coordinates and
cached exact-length arrays. Before measuring, it verifies that the two approaches rasterize
to identical pixels. Across three runs of 3,000 iterations with tiering disabled, individual
`MoveTo`/`LineTo` calls average 0.417, 0.400 and 0.415 ms for all ten paths. Bulk `AddPoly`
averages 0.0064, 0.0066 and 0.0085 ms. Both allocate zero managed bytes per steady-state
iteration. The median relative improvement is approximately 63x, but the absolute saving is
only about **0.41 ms per ten-series path rebuild**. This cannot by itself explain a roughly
9 ms chart measurement cost. It excludes coordinate fetching/scaling, gaps, rasterization,
resizing arrays when the representative count changes, and UI scheduling. Production bulk
path changes were deferred to keep the experiments isolated.

Evidence: `artifacts/path-build-{1,2,3}.json` and `tests/Benchmarks/PathBuildBenchmark.cs`.

### 7. Attribute actual fixed-viewport chart measurement

`StreamingMeasureBenchmark` uses the target waveform `series + sin(sampleIndex * 0.005)`,
ten series with one million initial measurements each, and ten appended samples per series
before every measured update. The 1000-by-600 chart has fixed preallocated X limits and fixed
Y limits. Three fresh-process runs measure 300 updates each with tiering disabled and internal
diagnostic stopwatches enabled. This measures the actual chart `Measure` call, including the
renderer; it excludes software drawing and Avalonia scheduling.

| Run | Total Measure mean | Selection mean | Path construction mean | Other Measure mean |
| --- | ---: | ---: | ---: | ---: |
| 1 | 15.737 ms | 0.194 ms | 11.214 ms | 4.329 ms |
| 2 | 6.213 ms | 0.063 ms | 4.733 ms | 1.417 ms |
| 3 | 11.268 ms | 0.107 ms | 8.472 ms | 2.689 ms |

All times are for all ten series per update. Every run records **zero full selections and
3,000 partial selections**, confirming that every series/update uses the prefix cache. Each
series ends with 3,430 representatives. Selection is approximately 1% of total measurement
time; rebuilding paths accounts for 71–76%. Absolute timings vary considerably with the
machine, but the attribution is consistent across all three runs.

This falsifies the hypothesis that frequent full-selection cache misses explain the remaining
measurement cost. The next candidate is a renderer-owned coordinate prefix cache: reuse
already transformed points when the scale and prefix remain unchanged, update only the dirty
suffix, and invalidate on generation, viewport, scale or inversion changes. Current `BuildPath`
fetches the original model plus X/Y for every representative even though rendering uses only
X/Y. An internal coordinates-only accessor could remove that unnecessary model read.

Evidence: `artifacts/streaming-measure-profile-{1,2,3}.json` and
`tests/Benchmarks/StreamingMeasureBenchmark.cs`. These results do not establish 60 FPS.

### 8. Reuse pixel coordinates for the unchanged representative prefix

The renderer now retains per-view pixel coordinates and segment-start flags. On a fixed-view
append, it recomputes from one representative before the dirty suffix, preserving gap joins.
Scale, viewport and generation changes force the appropriate full recomputation. Native
`MoveTo`/`LineTo` construction remains unchanged, isolating the effect of coordinate reuse.
The source adds an internal `GetXY` accessor that reads the cached X/Y arrays without copying
the original model. This adds no public source API and does not change tooltip model identity.

The entire old benchmark executable directory is preserved at
`artifacts/benchmark-before-coordinatecache-out`. Three paired fresh-process comparisons use
identical workload/runtime settings and alternate order: baseline/candidate, candidate/baseline,
baseline/candidate. Each process measures 300 updates after warmup.

| Pair | Baseline Measure mean | Candidate Measure mean | Baseline path mean | Candidate path mean |
| --- | ---: | ---: | ---: | ---: |
| 1 | 5.867 ms | 1.728 ms | 4.393 ms | 0.673 ms |
| 2 | 5.337 ms | 1.736 ms | 4.082 ms | 0.670 ms |
| 3 | 5.781 ms | 1.524 ms | 4.314 ms | 0.576 ms |

All times cover ten series containing 10M initial samples in total. Every run retains zero full
selections, 3,000 partial selections and 3,430 final representatives per series. Total measurement
improves 67–74% in each pair; path construction improves 84–87%. The median of process means
improves from **5.781 to 1.728 ms**, and candidate p95 values span **2.12–2.85 ms**.
This supports coordinate reuse as the dominant remaining measurement improvement, rather
than bulk native path calls alone. The renderer retains two additional capacity arrays, one
eight-byte `SKPoint` and one Boolean per slot; their capacity follows peak viewport representation
size rather than raw history length.

All 14 focused source, streaming, viewport-animation and raster-cache tests passed before this
comparison. The source mapping test also verifies `GetXY` returns cached coordinates after the
original model is mutated. Actual Avalonia validation is still necessary: these timings exclude
rasterization, dispatcher scheduling and presentation.

Evidence: `artifacts/coordinate-cache-{baseline,candidate}-{1,2,3}.json`,
`artifacts/pixel-cache-tests.log` and `artifacts/coordinate-cache-profile-build.log`.

## Validation

The actual MSTest 4.0.2 test executable ran under .NET 8.0.25. The full CoreTests run produced
850 passes and two failures out of 852 tests. The two failures are existing locale assumptions
in `LabelerTesting.Micra` (expects a decimal dot) and `LabelerTesting.Currency` (expects `$`)
on this German-culture machine; no labeler code changed in this experiment.

All six initial indexed-source tests and both streaming-renderer tests passed. After the bitwise
query change and after the Block8 change, these eight tests passed again against freshly built
core assemblies. The additional suffix-join regression brings the focused suite to nine tests;
all nine passed after `SelectFromBucket` was implemented.
An additional renderer regression then compares fresh and cached charts pixel-for-pixel across
ten append batches, duplicate X, gaps, an unchanged measure, pan/zoom, resize, inverted X,
appends beyond the viewport, clear-to-empty and repopulation. All ten focused tests pass.
Coverage includes brute-force bucket selection and exact viewport bounds across append,
block, hierarchy and chunk boundaries; duplicate timestamps; cached mapping and original
model identity; gaps and reset; invalid input; extreme finite X; bounded renderer visuals;
raw tooltip identity; stale reset epochs; visible raster gaps; and remove/re-add lifecycle.

Evidence: `artifacts/indexed-tests-run.log`, `artifacts/indexed-test-results/*.trx`,
`artifacts/indexed-bitmask-tests.log`, `artifacts/indexed-bitmask-test-results/*.trx`.
Additional results are in `artifacts/indexed-block8-tests.log` and
`artifacts/indexed-suffix-tests.log`, with corresponding TRX directories.
The renderer cache regression result is `artifacts/indexed-rendercache-tests.log`.

`dotnet test` failed in build orchestration without useful project diagnostics on the installed
.NET 11 preview SDK. Direct `dotnet build` with the repository's safe environment succeeded;
running the resulting `CoreTests.dll` exercised the real MSTest suite and generated TRX output.

## Remaining experiments

- Validate the combined source, coordinate-prefix and raster caches in actual Avalonia runs.
- Keep source-operation latency separate from software raster time and actual Avalonia frame
  intervals. These microbenchmarks alone cannot establish the user's 60 FPS target.

## Allocation sampling after coordinate caching

A separate EventPipe diagnostic (`artifacts/alloc-full-pointer.json`, raw
`.nettrace` and `.etlx` retained) collected 1,988 allocation ticks, all with managed
stacks, zero lost events, and 202.6 MiB weighted allocation over ten requested
seconds. The full-viewport 10M Avalonia workload included pointer movement,
synchronous updates, and raster caching. Profiling timings are not acceptance
results. The reusable collector is `tests/AllocationProbe`.

The largest sampled types were PointMotionProperty (10.2%), FloatMotionProperty
(9.3%), String (8.3%), LabelGeometry (6.3%), and PaddingMotionProperty (3.4%).
Dominant stacks construct BaseLabelGeometry via CoreAxis.GetPossibleMaxLabelSize
and GetPossibleSize, then tokenize/shape text. Within the saved top 80 stacks,
GetPossibleSize accounts for at least 40.2% of weighted bytes, including at least
31.4% through GetPossibleMaxLabelSize. DefaultTooltip stacks account for at least
11.7%. These are lower bounds, and the nested axis categories overlap.

This identifies axis label measurement reuse as a concrete next hypothesis.
Reusing a scratch label geometry or caching measured sizes must still honor
changed label text, formatter behavior, font/paint, padding, rotation, and separate
chart views. Allocation evidence alone does not establish the cause of missed
frames; paired timing and correctness checks remain necessary.
