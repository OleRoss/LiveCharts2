param(
    [string]$BaselineDirectory = 'artifacts/avalonia-before-axis-cache-bin',
    [string]$CandidateDirectory = 'artifacts/avalonia-bin',
    [string]$OutputPrefix = 'artifacts/avalonia-axis-ab',
    [int]$Seconds = 15
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $workspaceRoot
try {
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    # Preserve the caller's tiered-compilation setting for all four processes.
    # Both executables record it in their JSON; null means the runtime default.
    $variants = @(
        @{ Name = 'baseline-1'; Directory = $BaselineDirectory },
        @{ Name = 'candidate-1'; Directory = $CandidateDirectory },
        @{ Name = 'candidate-2'; Directory = $CandidateDirectory },
        @{ Name = 'baseline-2'; Directory = $BaselineDirectory }
    )
    foreach ($variant in $variants) {
        $resultPrefix = "$OutputPrefix-$($variant.Name)"
        $benchmarkArguments = @('--mode', 'streaming', '--points', '1000000',
            '--seconds', $Seconds, '--viewport', 'full', '--animation-ms', '100',
            '--update-ms', '16', '--scheduler', 'raf', '--every-frame',
            '--manual-updates', '--synchronous-measure', '--force-update', '--pointer',
            '--raster-cache', '--one-pixel-stroke', '--high-resolution-timer',
            '--label', $variant.Name, '--output', "$resultPrefix.json")
        $executable = (Resolve-Path (Join-Path $variant.Directory 'AvaloniaStreamingBench.exe')).Path
        $benchmarkProcess = Start-Process -FilePath $executable -ArgumentList $benchmarkArguments `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput "$resultPrefix.stdout.log" `
            -RedirectStandardError "$resultPrefix.stderr.log"
        $benchmarkProcess.WaitForExit()
        if ($benchmarkProcess.ExitCode -ne 0) {
            throw "Benchmark $($variant.Name) failed; inspect $resultPrefix.stderr.log."
        }
        $result = Get-Content "$resultPrefix.json" -Raw | ConvertFrom-Json
        [pscustomobject]@{
            Variant = $variant.Name
            DrawFps = $result.chartDrawFps
            SourceChangedFps = $result.sourceChangedDrawFps
            MeasureMs = $result.chartMeasureWorkMs.mean
            DrawMs = $result.chartGeometryDrawWorkMs.mean
            AllocatedBytes = $result.allocatedBytes
        } | ConvertTo-Json -Compress
    }
}
finally {
    Pop-Location
}
