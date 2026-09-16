param([switch]$Restore)

$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..\..')
try {
    $env:DOTNET_CLI_HOME = Join-Path (Get-Location).Path 'artifacts\dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path (Get-Location).Path 'artifacts\nuget'
    $env:GIT_CONFIG_GLOBAL = Join-Path (Get-Location).Path 'artifacts\gitconfig'
    New-Item -ItemType Directory -Force artifacts | Out-Null
    git config --global --add safe.directory (Get-Location).Path.Replace('\', '/')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($Restore) {
        dotnet restore tests\Benchmarks\Benchmarks.csproj -nodeReuse:false -maxcpucount:1 --verbosity minimal
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $benchmarkOutput = (Join-Path (Get-Location).Path 'artifacts\benchmark-baseline-out') + '\'
    dotnet build tests\Benchmarks\Benchmarks.csproj -c Release --no-restore -nodeReuse:false -maxcpucount:1 --verbosity minimal /p:UseSharedCompilation=false /p:OutDir=$benchmarkOutput
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
