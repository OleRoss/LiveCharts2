"""Build the self-contained HTML summary from saved local benchmark evidence."""
from pathlib import Path
import base64
import datetime
import html
import json

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts"
OUT = Path(__file__).with_name("report.html")


def escape(value):
    return html.escape(str(value))


def number(value, digits=2):
    return "n/a" if value is None else f"{value:,.{digits}f}"


def table(headers, rows):
    return "<div class='table-scroll'><table><thead><tr>" + "".join(
        f"<th>{escape(h)}</th>" for h in headers
    ) + "</tr></thead><tbody>" + "".join(
        "<tr>" + "".join(f"<td>{escape(c)}</td>" for c in row) + "</tr>" for row in rows
    ) + "</tbody></table></div>"


records = []
for pattern in ("stream-*.json", "index-*.json", "avalonia-*.json", "raster-*.json", "path-build-*.json", "streaming-measure-*.json", "coordinate-cache-*.json", "axis-scratch-*.json", "alloc-*.json", "frame-trace*-analysis.json"):
    for path in sorted(ARTIFACTS.glob(pattern)):
        records.append((path.name, json.loads(path.read_text(encoding="utf-8-sig"))))

core_rows, ui_rows, ui_phase_rows, index_rows, raster_rows, raw = [], [], [], [], [], []
measure_rows = []
allocation_rows = []
for name, data in records:
    if not isinstance(data, dict):
        raw.append(f"<details><summary>{escape(name)}</summary><pre>{escape(json.dumps(data, indent=2))}</pre></details>")
        continue
    if data.get("kind") == "retained-core-cpu-skia":
        frames = data.get("frames", [])
        phase = lambda key: sum(f.get(key, 0) for f in frames) / len(frames) if frames else None
        core_rows.append([
            name, data.get("mode"), f"{data['pointsPerSeries'] * data['seriesCount']:,}",
            data.get("viewport", "full"), number(data.get("meanMs")), number(data.get("p95Ms")),
            number(data.get("p99Ms")), number(phase("selectionMs")), number(phase("measureMs")),
            number(phase("drawMs")), number(phase("hitTestMs"), 3),
            f"{data.get('overBudgetCount')}/{data.get('iterations')}",
        ])
    elif "chartDrawFps" in data:
        options = data.get("options", {})
        ui_rows.append([
            name, "Diagnostic" if options.get("Diagnostics") or options.get("FrameTrace") else "Timing", data.get("retainedSamples"), options.get("Viewport", "full"),
            number(data.get("chartDrawFps")), number(data.get("sourceChangedDrawFps")),
            number(data.get("chartDrawIntervalMs", {}).get("p95")),
            number(data.get("uiCallbackFps")), number(data.get("tooltipQueryMs", {}).get("p95"), 3),
            number(data.get("oldestPendingBatchAgeMs", {}).get("p95")),
            data.get("pendingBatches", "unrecorded"), number(data.get("allocatedBytes", 0) / 1048576),
            number(data.get("workingSetBytes", 0) / 1048576),
        ])
        ui_phase_rows.append([name, number(data.get("chartMeasureWorkMs", {}).get("mean")),
                              number(data.get("chartMeasureWorkMs", {}).get("p95")),
                              number(data.get("chartGeometryDrawWorkMs", {}).get("mean")),
                              number(data.get("chartGeometryDrawWorkMs", {}).get("p95")),
                              data.get("syntheticCorePointerMoves", 0),
                              data.get("tooltipShowWorkMs", {}).get("count", 0),
                              number(data.get("tooltipShowWorkMs", {}).get("p95")),
                              number(data.get("latestMeasuredBatchAgeAtDrawMs", {}).get("p95"))])
    elif data.get("kind") == "eventpipe-sampled-managed-allocations":
        for item in data.get("types", [])[:12]:
            allocation_rows.append([name, item.get("type"), number(item.get("estimatedAllocatedBytes", 0) / 1048576),
                                    number(item.get("percent")), item.get("samples"), data.get("lostEvents")])
    elif data.get("kind") == "actual-streaming-measure-attribution":
        counters = data.get("counters", {})
        measure_rows.append([
            name, number(data.get("totalMeasureMeanMs")), number(data.get("p95Ms")),
            number(data.get("allocatedBytesPerMeasureMean", 0) / 1024) if "allocatedBytesPerMeasureMean" in data else "n/a",
            number(data.get("selectionMeanMs"), 3), number(data.get("pathBuildMeanMs"), 3),
            number(data.get("otherChartMeasureMeanMs"), 3), counters.get("FullSelectionCount"),
            counters.get("PartialSelectionCount"), counters.get("UnchangedSelectionCount"),
        ])
    elif data.get("kind") == "indexed-source-microbenchmark":
        for result in data.get("results", []):
            index_rows.append([name, data.get("originalCount"), result.get("name", result.get("operation")),
                               number(result.get("meanMs", 0) * 1000, 3), number(result.get("p95Ms", 0) * 1000, 3),
                               number(data.get("buildMs")), number(data.get("retainedBytes", 0) / 1048576)])
    elif "raster" in data.get("kind", "") or data.get("kind") == "path-construction-only":
        for result in data.get("results", data.get("cases", [])):
            raster_rows.append([name, result.get("operation", result.get("name", result.get("mode",
                                f"{result.get('algorithm')} · AA {result.get('antialias')} · width {result.get('strokeWidth')}"))),
                                number(result.get("meanMs")), number(result.get("p95Ms")),
                                number(result.get("p99Ms"))])
    raw.append(f"<details><summary>{escape(name)}</summary><pre>{escape(json.dumps(data, indent=2))}</pre></details>")

visuals = []
for filename, caption in [
    ("avalonia-mcp-full-tooltip.png", "The Avalonia diagnostic window shows ten measurement series and the complete tooltip in the earlier default style. Avalonia MCP captured this image. This run does not count toward performance acceptance."),
    ("raster-scale125-stroke08-run1-path-aaTrue-stroke0,8.png", "Connected selected path, positive one-device-pixel stroke, 125% scale."),
    ("raster-envelope-scale125-run1-envelope-aaTrue-stroke1.png", "Experimental filled min/max columns change coverage and show seams between columns. This renderer was not adopted.")
]:
    path = ARTIFACTS / filename
    if path.exists():
        encoded = base64.b64encode(path.read_bytes()).decode("ascii")
        visuals.append(f'<figure><img style="width:100%;height:auto" src="data:image/png;base64,{encoded}" alt="{escape(caption)}"><figcaption>{escape(caption)}</figcaption></figure>')

notes_path = Path(__file__).with_name("report-notes.json")
notes = json.loads(notes_path.read_text(encoding="utf-8-sig")) if notes_path.exists() else {
    "status": "Experiment summary unavailable. Read the raw results below.",
    "summary": "The first indexed-source and packed-path prototype improves throughput and tooltip latency, but the target remains unmet. Further controlled experiments are running.",
    "changes": [], "findings": [], "validation": [], "limitations": [],
}

target_rows = []
for name, data in records:
    if name in ("avalonia-sustained-shadowoff-75.json", "avalonia-final-clean-75.json"):
        intervals = data.get("sourceChangedDrawIntervalMs", {})
        target_rows.append([name, number(data.get("elapsedSeconds")),
                            number(data.get("sourceChangedDrawFps")), number(data.get("chartDrawFps")),
                            number(intervals.get("p95")), number(intervals.get("p99")), number(intervals.get("max")),
                            f"{data.get('consumedBatches')} / {data.get('producedBatches')}",
                            data.get("pendingBatches")])


def bullets(items):
    return "<ul>" + "".join(f"<li>{escape(item)}</li>" for item in items) + "</ul>"


template = """<!doctype html>
<html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>LiveCharts2 · streaming performance investigation</title>
<style>
:root{color-scheme:light;--ink:#142735;--muted:#566b78;--line:#d9e4e8;--blue:#126779;--paper:#f4f7f8}
*{box-sizing:border-box}body{margin:0;background:var(--paper);color:var(--ink);font:16px/1.6 system-ui,Segoe UI,sans-serif}
header{background:#102d3a;color:white;padding:64px max(6vw,24px) 48px}header small{color:#93d4d4;text-transform:uppercase;letter-spacing:.16em}
h1{font-size:clamp(30px,4vw,56px);line-height:1.1;font-weight:650;max-width:1050px;margin:20px 0}header p{max-width:870px;color:#d1e1e7}
main{max-width:1450px;margin:auto;padding:36px max(3vw,20px) 70px}section{background:white;border:1px solid var(--line);border-radius:12px;padding:26px;margin:20px 0}
h2{margin:0 0 16px;font-size:25px}h3{font-size:18px}p{max-width:1100px}a{color:var(--blue)}.status{border-left:5px solid #d99437;background:#fff9ec;padding:16px 20px;margin-bottom:20px}.muted{color:var(--muted)}
.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:15px}.card{border:1px solid var(--line);padding:20px;border-radius:8px;background:white}.card strong{display:block;color:var(--blue);font-size:30px}.card span{font-size:14px;color:var(--muted)}
.table-scroll{overflow:auto}table{width:100%;border-collapse:collapse;font-size:13px;font-variant-numeric:tabular-nums}th{background:#edf4f6;text-align:left;white-space:nowrap}td,th{padding:10px;border-bottom:1px solid var(--line);vertical-align:top}td:first-child{max-width:340px;overflow-wrap:anywhere}tr:hover td{background:#f6fafb}
pre{overflow:auto;font:12px/1.5 ui-monospace,Consolas,monospace;background:#102d3a;color:#e0edf0;padding:18px;border-radius:8px;max-height:600px}details{border-top:1px solid var(--line);padding:10px 0}summary{cursor:pointer;font-size:14px}li{margin:8px 0}code{background:#eaf1f3;border-radius:4px;padding:2px 5px}.legend{font-size:13px;color:var(--muted)}
@media(max-width:750px){.cards{grid-template-columns:repeat(2,1fr)}section{padding:18px}}@media print{body{background:white}header{padding:25px;color:black;background:white}header p{color:#444}section{break-inside:avoid}details{display:none}table{font-size:9px}.table-scroll{overflow:visible}}
</style>
<header><small>Local fork · CPU / software rendering · September 2026</small>
<h1>LiveCharts2 at 10 million samples</h1>
<p>What changed, what helped, and why the final Avalonia run landed at 58.55 FPS. Base commit <code>8537b90ffb100a947cd0b1f6be49f896ae6050eb</code>.</p></header>
<main><div class="status"><strong>@@STATUS@@</strong><br>@@SUMMARY@@</div>
<div class="cards"><div class="card"><strong>10M</strong><span>retained samples total, 1M per channel</span></div><div class="card"><strong>10,000/s</strong><span>10 samples × 10 series every 10 ms</span></div><div class="card"><strong>60 FPS</strong><span>target; actual results distinguished below</span></div><div class="card"><strong>CPU</strong><span>Skia software rasterization; GPU out of scope</span></div></div>
<section><h2>The two long runs</h2><p>Each run started with 10M samples across ten series, then added ten samples per series every 10 ms. Both used a fixed overview, CPU raster caching and one-device-pixel strokes. The application updated on each available UI frame. Tooltips and 100 ms animation remained enabled, with tooltip shadows off.</p><p>New-data FPS counts only draws that include a newer acquisition batch. The interval columns use milliseconds. The last column shows batches still queued when the run stopped.</p>@@TARGET@@<p>The final clean build removes temporary tracing hooks and sets dark tooltip text on a light background. These are chart drawing rates. We did not measure physical monitor presents.</p></section>
<section><h2>Outcome and evidence</h2>@@FINDINGS@@<p class="muted">Reference machine: Intel Core i7-1265U, 12 logical processors, Windows build 26200. Core benchmark runtime: .NET 8.0.25; Avalonia: .NET 10.0.10. Exact dimensions, configuration, timestamps and per-iteration evidence are embedded below. Laptop frequency, thermal state and other desktop applications are not controlled.</p></section>
<section><h2>What we tested</h2><ul>
<li>Remove costly styling from ordinary lines, then compare them with an indexed source and a renderer that avoids one visual object per sample.</li>
<li>Cache extrema and unchanged pixel coordinates. Compare index block sizes and bit-mask traversal.</li>
<li>Compare connected paths, separate segments and min/max rectangles. Vary antialiasing, stroke width and display scaling.</li>
<li>Cache rendered pixels for stable viewports. Check gaps, paint changes, resizing and animation against fresh rendering.</li>
<li>Compare update scheduling and count frames with new data. Trace the time between UI updates and drawing.</li>
<li>Measure axis and tooltip allocations. Test axis geometry reuse and isolate the tooltip shadow cost.</li>
</ul><h3>Changes kept in the fork</h3>@@CHANGES@@</section>
<section><h2>Retained CPU chart measurements</h2><p>Each measured iteration appends ten samples to each of ten series, measures the retained chart, rasterizes to a reused software surface and performs one tooltip query. These are work-duration measurements, not Avalonia FPS. All phase values are milliseconds. Misses count iterations over 16.67 ms.</p>@@CORE@@
<p class="legend">The initial <code>stream-1000-default.json</code> may have overlapped UI smoke and is exploratory only; use <code>stream-1000-default-controlled.json</code> for the baseline comparison. Short exploratory runs include tiered JIT effects. Stable A/B filenames identify later controlled experiments. Workloads with different point counts, styles, viewports or runtime settings are not direct A/B pairs.</p></section>
<section><h2>Avalonia frame cadence and acquisition</h2><p>A real desktop window and software renderer. Chart-draw callbacks, UI frame callbacks and physical monitor presents are different events. This harness does not instrument physical presentation. Tooltip numbers measure lookup unless a run explicitly enables actual tooltip presentation. Allocation is total managed allocation over the measured run; working set is a process snapshot. Age is the p95 age of the oldest pending batch, in milliseconds.</p>@@UI@@<h3>Completed work and tooltip lifecycle</h3><p>All durations are milliseconds. Freshness measures the age of the latest measured acquisition batch when the chart draw finishes. Pointer counts refer to synthetic core pointer moves, not operating-system mouse events.</p>@@UI_PHASES@@
<p class="legend">Early baseline/smoke runs used different dimensions/signals and an initial scheduling counter; later runs use a background producer that queues actual batches. Read each embedded configuration before comparison. Pending batches at shutdown are reported, not silently counted as displayed samples. A callback average close to 60 alone is insufficient: inspect interval tails, data age and completed work.</p></section>
<section><h2>Where chart measurement time goes</h2><p>Ten streaming series with a fixed viewport. Instrumented synchronous measurement separates viewport selection, path construction and remaining chart work. Times are milliseconds; selection counters count series calls across the run. These measurements exclude drawing and UI scheduling.</p>@@MEASURE@@</section>
<section><h2>Indexed source microbenchmarks</h2><p>One source, independent of rendering. Operation times are microseconds; construction is milliseconds. The raw history and mapping cache remain retained. Append and nearest-X queries do not scan the history. The initial source uses 32-sample leaf blocks; variant names record experiments.</p>@@INDEX@@</section>
<section><h2>Where allocations come from</h2><p>A separate 10-second EventPipe capture recorded 1,988 samples, all with stacks, and zero lost events. The table estimates allocation by sampled object type. It does not measure retained heap size or count every allocation.</p><p>Among the top 80 stacks, axis label measurement accounts for at least 40.2% of estimated allocation. That includes 31.4% from maximum-label estimation. Tooltip code accounts for at least 11.7%. Nested calls overlap. The <a href="../../tests/AllocationProbe/README.md">AllocationProbe instructions</a> explain how to repeat the capture.</p>@@ALLOCATIONS@@</section>
<section><h2>Software drawing experiments</h2><p>These tests select the samples before timing the drawing loop. Different drawing shapes can change joins and antialiasing. Disabling antialiasing changes appearance too. The saved configurations and images show those differences.</p>@@RASTER@@<details><summary>Application screenshot and drawing comparisons</summary>@@VISUALS@@</details></section>
<section><h2>Developer experience and fidelity</h2><p>The opt-in <code>TimeSeriesBuffer&lt;T&gt;</code> accepts X/Y selectors and maps each appended model once. <code>StreamingLineSeries&lt;T&gt;</code> uses the existing engine override for drawing, bounds and hit-testing. Original models and indices remain available for tooltip formatting. The source is append-only and caller-synchronized; arbitrary model mutations do not invalidate cached coordinates.</p>
<p>Selection retains chronological first/minimum/maximum/last samples and a gap indicator per horizontal bucket, plus viewport neighbors. This preserves selected extrema and sample identity. It reduces subpixel detail and does not promise identical antialiased pixels. Gap queries prevent lines joining across omitted missing-data samples. Raw measurements are never replaced by the display representation.</p>
<p>The prototype keeps axes, themes, paints and formatting. It does not support fills, markers, smoothing, stacking, data labels, per-sample transitions, historical edits or automatic retention. Some inherited properties still appear in the API even though the streaming renderer ignores them. That needs work before upstreaming.</p>@@LIMITATIONS@@
<p>Practical data ownership, scheduling and API examples: <a href="../streaming-performance.md">streaming-performance.md</a>.</p></section>
<section><h2>Validation and reproduction</h2>@@VALIDATION@@<p>Benchmark entry points: <a href="../../tests/Benchmarks/README.md">CPU and source benchmark README</a> and <a href="../../tests/AvaloniaStreamingBench/README.md">Avalonia harness README</a>. Restore/build scripts keep temporary files under <code>artifacts/</code>. Run benchmarks serially on the same machine, with the same runtime and configuration. Preserve raw output before changing implementations.</p>
<p>Prepared research context: <a href="https://chatgpt.com/c/6aa98a9e-12bc-83eb-b495-ab5abd78a52a">Live Charts Performance bottlenecks</a>. The research supplied architectural hypotheses; performance numbers in this report come from this local fork.</p></section>
<section><h2>Where to resume</h2><p>Work stopped at the agreed point. The next useful step is to explain the final run's slowdown after 60 seconds. Then repeat the final settings with moving views. Paged index storage is another idea worth testing, but we have no measurements for it yet. The <a href="README.md">checkpoint notes</a> contain commands and saved diagnostic patches.</p></section>
<section><h2>Embedded raw evidence</h2><p>Saved benchmark JSON results are included for audit, including exploratory and failed-target runs. Expand a file to inspect configuration and samples. Generated @@DATE@@.</p>@@RAW@@</section></main></html>"""

replacements = {
    "STATUS": escape(notes["status"]), "SUMMARY": escape(notes["summary"]),
    "FINDINGS": bullets(notes.get("findings", [])), "CHANGES": bullets(notes.get("changes", [])),
    "VALIDATION": bullets(notes.get("validation", [])), "LIMITATIONS": bullets(notes.get("limitations", [])),
    "TARGET": table(["Run", "Seconds", "New-data FPS", "Draw FPS", "Interval p95", "p99", "Max", "Consumed / produced batches", "Queued at stop"], target_rows),
    "CORE": table(["File / variant", "Mode", "Retained start", "View", "Mean", "p95", "p99", "Select", "Measure", "Draw", "Tooltip", "Misses"], core_rows),
    "UI": table(["Run", "Purpose", "Retained end", "View", "Draw FPS", "New-data FPS", "Interval p95", "UI FPS", "Tooltip p95", "Age p95", "Pending", "Allocated MiB", "Working MiB"], ui_rows),
    "ALLOCATIONS": table(["Run", "Sampled allocation type", "Estimated MiB", "%", "Samples", "Lost events"], allocation_rows),
    "UI_PHASES": table(["Run", "Measure mean", "Measure p95", "Draw mean", "Draw p95", "Pointer moves", "Tooltip shows", "Show p95", "Freshness p95"], ui_phase_rows),
    "MEASURE": table(["Run", "Total mean", "Total p95", "Allocated KiB/call", "Selection", "Path", "Other", "Full selections", "Tail selections", "Unchanged"], measure_rows),
    "INDEX": table(["Run", "Retained start", "Operation", "Mean µs", "p95 µs", "Build ms", "Retained MiB"], index_rows),
    "RASTER": table(["Run", "Case", "Mean ms", "p95 ms", "p99 ms"], raster_rows),
    "VISUALS": "".join(visuals),
    "RAW": "".join(raw), "DATE": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
}
for key, value in replacements.items():
    template = template.replace(f"@@{key}@@", value)
OUT.write_text(template, encoding="utf-8")
print(f"Wrote {OUT} ({OUT.stat().st_size:,} bytes; {len(records)} evidence files)")
