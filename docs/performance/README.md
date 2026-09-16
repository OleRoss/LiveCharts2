# Performance investigation checkpoint

Stopped at the user's requested checkpoint on 2026-09-16. No further experiments are running.

## Results

Read [the self-contained HTML report](report.html) for all experiments, embedded raw
statistics, visual tradeoffs and validation. The final clean 75-second Avalonia run
reached **58.55 frames/s containing new samples**; the earlier long run reached **61.25**.
Both started with 10M samples across ten series and acquired ten samples per series
every 10 ms. Repeatable strict 60 FPS remains open.

The measured configuration uses a fixed overview, indexed append-only storage,
one-device-pixel strokes, CPU raster caching, manual frame-paced updates and shadow-free
tooltips. The final build explicitly uses readable dark tooltip text on a light background.
This is not a default-styling or moving-viewport 60 FPS claim.

## Reproduction and retained evidence

- [Application guidance and target command](../streaming-performance.md)
- [CPU, source and raster benchmarks](../../tests/Benchmarks/README.md)
- [Avalonia harness and paired comparison](../../tests/AvaloniaStreamingBench/README.md)
- [Allocation sampling collector](../../tests/AllocationProbe/README.md)
- [Detailed index experiments](index-findings.md)

Raw JSON, images, traces and test logs remain in the local `artifacts/` directory.
The HTML embeds the saved JSON and selected images, so its evidence remains readable
without those separate files. Regenerate it from the repository root with:

```powershell
python docs/performance/generate_report.py
```

Temporary production frame tracing was removed. The two `frame-trace-*.patch` files
preserve opt-in instrumentation for reproducing that diagnostic experiment; apply both
to the matching checkpoint, follow the harness documentation, and remove them afterward.
`analyze-frame-trace.ps1` summarizes the saved event sequences. Diagnostic results must
remain separate from performance acceptance trials.

Validation: full suite **868/870 passed**, with two pre-existing culture-format failures;
final focused checks **27/27 passed**. Production .NET Standard 2.0 and .NET Framework
4.6.2 builds and the clean Avalonia build passed. Raster-cache validation passed 15 cases.

## Remaining work, if resumed

1. Attribute the final run's slowdown after 60 seconds; do not assume its cause is GC,
   thermal behavior or another process without evidence. The capacity-growth boundary
   happened earlier, while cadence remained close to its prior level.
2. Repeat the final configuration under measured system load, then validate moving and
   automatically expanding viewports with the same manual-update settings.
3. Consider paged index summaries to avoid contiguous-array growth copies. This was
   reviewed but **not implemented or benchmarked**; added query indirection needs testing.
4. Investigate tooltip layout reuse and further axis text-shaping savings while preserving
   mutable fonts, formatter behavior and resource ownership.
5. Refine the experimental series' inherited unsupported API surface before upstreaming.

No GPU work, multi-hour soak or physical monitor-present measurement was performed.
