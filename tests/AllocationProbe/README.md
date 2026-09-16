# Managed allocation diagnostic

This small EventPipe collector attaches to a running .NET chart benchmark and
reports sampled allocation bytes by type and managed stack. It is a diagnostic
tool: run it separately from acceptance FPS measurements.

Build with the sandbox-safe .NET environment in root `AGENTS.md`, including
workspace-local `NUGET_PACKAGES`, then:

```powershell
dotnet restore tests/AllocationProbe/AllocationProbe.csproj
dotnet build tests/AllocationProbe/AllocationProbe.csproj -c Release --no-restore -nodeReuse:false -maxcpucount:1 /p:UseSharedCompilation=false
dotnet tests/AllocationProbe/bin/Release/net8.0/AllocationProbe.dll <pid> 10 artifacts/alloc-full-pointer
```

Launch the target first, wait until initial data loading and warmup finish, then
attach. Keep it alive for the capture plus rundown. Duration accepts 1–60 seconds.
The target and collector must run under an identity permitted to access the
runtime diagnostic pipe. In the Windows sandbox used for this experiment, both
processes needed approved elevated execution; mixing identities failed.

Outputs are JSON, raw `.nettrace`, converted `.etlx`, and a conversion log under
the supplied prefix. Local managed method metadata supplies stack names; no
symbol-server requests are made. The JSON includes lost events and stack coverage.

`GCAllocationTick` samples roughly allocation-sized intervals. Its allocation
amount weights estimate bytes attributed to the sampled object's type and stack;
these are **not exact per-type byte accounting**, native allocation sizes, or
retained heap measurements. The top 80 stacks are saved, so sums over that list
are lower bounds. Capture/rundown can affect application pacing.

The validated capture is `artifacts/alloc-full-pointer.json`: 1,988 samples,
1,988 stacks, zero lost events, 202.6 MiB weighted over 10 requested seconds.
It used the 10M full-viewport Avalonia harness with pointer movement, synchronous
updates, fixed bounds, raster cache, and no developer-tools UI.
