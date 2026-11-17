<#
.SYNOPSIS
  OptiCore build helper.

.EXAMPLE
  ./build.ps1                  Build everything (Debug).
  ./build.ps1 test             Run the unit tests.
  ./build.ps1 serve -DryRun    Development server for the UI at http://127.0.0.1:5317 in simulation mode.
  ./build.ps1 run              Start the desktop app.
  ./build.ps1 publish          Self-contained single file: ./release/OptiCore.exe (the UI is embedded).
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'test', 'serve', 'run', 'publish', 'clean')]
    [string]$Task = 'build',
    [string]$Configuration = 'Debug',
    [int]$Port = 5317,
    [switch]$DryRun,
    [string]$DataDir,
    [string]$Artifacts
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw 'The .NET 10 SDK was not found. Install it from https://dot.net/download' }

$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# A very deep checkout would push MSBuild's intermediate paths past what some tools accept,
# so the build output moves to the temp folder in that case.
if ($Artifacts) { $env:OPTICORE_ARTIFACTS = $Artifacts }
elseif (-not $env:OPTICORE_ARTIFACTS -and $root.Length -gt 110) { $env:OPTICORE_ARTIFACTS = Join-Path $env:TEMP 'opticore-build' }
$out = if ($env:OPTICORE_ARTIFACTS) { $env:OPTICORE_ARTIFACTS } else { Join-Path $root 'artifacts' }
$pivot = $Configuration.ToLowerInvariant() + '_win-x64'

function Invoke-Dotnet {
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE" }
}

switch ($Task) {
    'build' {
        Invoke-Dotnet build "$root\OptiCore.slnx" -c $Configuration -v m
    }
    'test' {
        Invoke-Dotnet test "$root\tests\OptiCore.Tests.csproj" -c $Configuration -v m
    }
    'serve' {
        Invoke-Dotnet build "$root\src\Cli\OptiCore.Cli.csproj" -c $Configuration -v q
        $serveArgs = @("$out\bin\OptiCore.Cli\$pivot\opticore-cli.dll", 'serve', '--port', $Port, '--ui', "$root\src\App\ui")
        if ($DryRun) { $serveArgs += '--dry-run' }
        if ($DataDir) { $serveArgs += @('--data-dir', $DataDir) }
        & $dotnet @serveArgs
    }
    'run' {
        Invoke-Dotnet build "$root\src\App\OptiCore.App.csproj" -c $Configuration -v q
        $runArgs = @()
        if ($DryRun) { $runArgs += '--dry-run' }
        if ($DataDir) { $runArgs += @('--data-dir', $DataDir) }
        & "$out\bin\OptiCore.App\$pivot\OptiCore.exe" @runArgs
    }
    'publish' {
        Invoke-Dotnet publish "$root\src\App\OptiCore.App.csproj" -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "$root\release"
        # API documentation files that NuGet packages carry along are not needed at run time.
        Get-ChildItem "$root\release\*.xml", "$root\release\*.pdb" -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
        if (Test-Path "$root\release\ui") { Remove-Item "$root\release\ui" -Recurse -Force }
        # The same binary acts as its own installer when its name contains "Kurulum" or "Setup".
        Copy-Item "$root\release\OptiCore.exe" "$root\release\OptiCore-Kurulum.exe" -Force
        Write-Host "Published: $root\release\OptiCore.exe (single file, no .NET installation needed)"
    }
    'clean' {
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    }
}
