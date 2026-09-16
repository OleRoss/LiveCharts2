using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: AllocationProbe <pid> <seconds:1..60> <output-prefix>");
    return 2;
}
var targetPid = int.Parse(args[0]);
var seconds = int.Parse(args[1]);
if (seconds < 1 || seconds > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
var prefix = Path.GetFullPath(args[2]);
Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
var tracePath = prefix + ".nettrace";
var etlxPath = prefix + ".etlx";
var jsonPath = prefix + ".json";
var keywords = ClrTraceEventParser.Keywords.GC |
    ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
    ClrTraceEventParser.Keywords.Stack |
    ClrTraceEventParser.Keywords.Loader |
    ClrTraceEventParser.Keywords.Jit;
var client = new DiagnosticsClient(targetPid);
var started = DateTimeOffset.UtcNow;
var captureWatch = Stopwatch.StartNew();
using (var session = client.StartEventPipeSession(
    new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)keywords),
    requestRundown: true, circularBufferMB: 128))
{
    await using var traceFile = File.Create(tracePath);
    var copy = session.EventStream.CopyToAsync(traceFile);
    Console.WriteLine($"Capturing allocation samples from process {targetPid} for {seconds}s.");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    await session.StopAsync(CancellationToken.None);
    await copy;
}
captureWatch.Stop();

var options = new TraceLogOptions
{
    LocalSymbolsOnly = true,
    ShouldResolveSymbols = _ => false,
    ConversionLogName = prefix + "-conversion.log"
};
TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath, options);
using var trace = new TraceLog(etlxPath);
using var source = trace.Events.GetSource();
var byType = new Dictionary<string, Totals>();
var byStack = new Dictionary<string, Totals>();
long ticks = 0;
long ticksWithStack = 0;
long weightedBytes = 0;
source.Clr.GCAllocationTick += sample =>
{
    if (sample.ProcessID != targetPid) return;
    var weight = sample.AllocationAmount64 > 0 ? sample.AllocationAmount64 : sample.AllocationAmount;
    if (weight <= 0) return;
    var type = string.IsNullOrWhiteSpace(sample.TypeName) ? $"<type 0x{sample.TypeID:x}>" : sample.TypeName;
    ticks++;
    weightedBytes += weight;
    Add(byType, type, weight, sample.ObjectSize);
    var frames = new List<string>();
    for (var stack = sample.CallStack(); stack is not null && frames.Count < 32; stack = stack.Caller)
    {
        var method = stack.CodeAddress.FullMethodName;
        frames.Add(string.IsNullOrEmpty(method) ? stack.CodeAddress.ToString() : method);
    }
    if (frames.Count != 0)
    {
        ticksWithStack++;
        Add(byStack, type + "\n" + string.Join("\n", frames), weight, sample.ObjectSize);
    }
};
source.Process();
var report = new
{
    kind = "eventpipe-sampled-managed-allocations",
    warning = "Separate diagnostic run, not an acceptance FPS result. GCAllocationTick weights estimate allocation bytes by the sampled allocation's type/stack; they are not exact per-type accounting or retained heap sizes. Capture and rundown add overhead.",
    targetPid,
    startedUtc = started,
    requestedSeconds = seconds,
    captureIncludingRundownSeconds = captureWatch.Elapsed.TotalSeconds,
    providerKeywords = $"0x{(long)keywords:x}",
    lostEvents = trace.EventsLost,
    allocationTicks = ticks,
    allocationTicksWithStack = ticksWithStack,
    estimatedAllocatedBytes = weightedBytes,
    estimatedBytesPerRequestedSecond = weightedBytes / (double)seconds,
    tracePath,
    etlxPath,
    types = byType.OrderByDescending(pair => pair.Value.WeightedBytes).Select(pair => new
    {
        type = pair.Key,
        estimatedAllocatedBytes = pair.Value.WeightedBytes,
        samples = pair.Value.Samples,
        sampledObjectBytes = pair.Value.SampledObjectBytes,
        percent = weightedBytes == 0 ? 0 : pair.Value.WeightedBytes * 100d / weightedBytes
    }).ToArray(),
    topStacks = byStack.OrderByDescending(pair => pair.Value.WeightedBytes).Take(80).Select(pair => new
    {
        typeAndStack = pair.Key.Split('\n'),
        estimatedAllocatedBytes = pair.Value.WeightedBytes,
        samples = pair.Value.Samples
    }).ToArray()
};
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Samples {ticks:N0}; stacks {ticksWithStack:N0}; weighted allocation {weightedBytes / 1048576d:F1} MiB; lost events {trace.EventsLost}. {jsonPath}");
return 0;

static void Add(Dictionary<string, Totals> totals, string key, long weighted, long objectSize)
{
    if (!totals.TryGetValue(key, out var value)) totals.Add(key, value = new Totals());
    value.WeightedBytes += weighted;
    value.SampledObjectBytes += objectSize;
    value.Samples++;
}

sealed class Totals
{
    public long WeightedBytes;
    public long SampledObjectBytes;
    public long Samples;
}
