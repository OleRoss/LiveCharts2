param(
    [ValidateSet('ordinary', 'streaming')][string]$Mode = 'streaming',
    [int]$PointsPerSeries = 1000000,
    [int]$Seconds = 15,
    [ValidateSet('full', 'live', 'auto')][string]$Viewport = 'full',
    [ValidateSet('timer', 'raf')][string]$Scheduler = 'raf',
    [int]$AnimationMs = 100,
    [int]$UpdateMs = 16,
    [switch]$ForceUpdate,
    [switch]$SynchronousMeasure,
    [switch]$EveryFrame,
    [switch]$ManualUpdates,
    [ValidateSet('default', 'on', 'off')][string]$TooltipShadow = 'default',
    [switch]$HighResolutionTimer,
    [switch]$OnePixelStroke,
    [switch]$RasterCache,
    [switch]$Pointer,
    [switch]$Diagnostics,
    [switch]$Restore,
    [switch]$SkipBuild,
    [string]$Output = 'artifacts/avalonia-result.json'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $workspaceRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $workspaceRoot 'artifacts/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $workspaceRoot 'artifacts/nuget'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:GIT_CONFIG_GLOBAL = Join-Path $workspaceRoot 'artifacts/gitconfig'
    New-Item -ItemType Directory -Force (Join-Path $workspaceRoot 'artifacts') | Out-Null
    git config --global --add safe.directory $workspaceRoot.Replace('\', '/')
    if ($LASTEXITCODE -ne 0) { throw 'Git safe-directory preflight failed.' }

    $project = Join-Path $PSScriptRoot 'AvaloniaStreamingBench.csproj'
    $outputDirectory = (Join-Path $workspaceRoot 'artifacts/avalonia-bin') + '/'
    if ($Restore) {
        dotnet restore $project -nodeReuse:false -maxcpucount:1 --verbosity minimal /p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    }
    if (-not $SkipBuild) {
        dotnet build $project --no-restore -c Release -nodeReuse:false -maxcpucount:1 --verbosity minimal /p:UseSharedCompilation=false "/p:OutDir=$outputDirectory"
        if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
    }
    $benchmarkArguments = @('--mode', $Mode, '--points', $PointsPerSeries, '--seconds', $Seconds,
        '--viewport', $Viewport, '--scheduler', $Scheduler, '--animation-ms', $AnimationMs,
        '--update-ms', $UpdateMs, '--output', $Output)
    $benchmarkArguments += @('--tooltip-shadow', $TooltipShadow)
    if ($ForceUpdate) { $benchmarkArguments += '--force-update' }
    if ($SynchronousMeasure) { $benchmarkArguments += '--synchronous-measure' }
    if ($EveryFrame) { $benchmarkArguments += '--every-frame' }
    if ($ManualUpdates) { $benchmarkArguments += '--manual-updates' }
    if ($HighResolutionTimer) { $benchmarkArguments += '--high-resolution-timer' }
    if ($OnePixelStroke) { $benchmarkArguments += '--one-pixel-stroke' }
    if ($RasterCache) { $benchmarkArguments += '--raster-cache' }
    if ($Pointer) { $benchmarkArguments += '--pointer' }
    if ($Diagnostics) { $benchmarkArguments += '--diagnostics' }
    & (Join-Path $outputDirectory 'AvaloniaStreamingBench.exe') @benchmarkArguments
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark execution failed.' }
}
finally {
    Pop-Location
}
