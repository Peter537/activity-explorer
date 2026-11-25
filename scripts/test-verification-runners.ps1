[CmdletBinding()]
param()

if ($WhatIfPreference) {
    [Console]::Error.WriteLine('Verification requires execution. PowerShell dry-run mode is active.')
    exit 1
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $null
$failure = $null
$originalMode = $env:AE_VERIFICATION_RUNNER_TEST_MODE
$originalTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$workerEvidence = [Collections.Generic.List[string]]::new()

function Assert-RunnerFailure([scriptblock]$action, [string]$expected) {
    try { & $action }
    catch {
        if ($_.Exception.Message -like "*$expected*") { return }
        throw
    }
    throw "Expected the runner to fail with: $expected"
}

function Get-OwnedWorker([string]$sample) {
    if (-not [IO.File]::Exists($sample)) { return }
    $identity = Get-Content -LiteralPath $sample -Raw | ConvertFrom-Json
    $worker = Get-Process -Id $identity.processId -ErrorAction SilentlyContinue
    if ($null -eq $worker) { return }
    try {
        if (-not $worker.HasExited -and $worker.StartTime.ToUniversalTime().Ticks -eq $identity.startTimeUtcTicks) {
            return $worker
        }
    } catch [InvalidOperationException] {
        if (-not $worker.HasExited) { throw }
    }
    $worker.Dispose()
}

$failureMessage = 'Could not create the temporary verification folder.'
try {
    $temp = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    $root = [IO.Directory]::CreateTempSubdirectory('activity-explorer-verification-')
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $failureMessage = 'Could not write the synthetic verification worker.'
    $project = Join-Path $root.FullName 'SyntheticWorker.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $project
    @'
using System.Diagnostics;
using System.Text.Json;

var mode = Environment.GetEnvironmentVariable("AE_VERIFICATION_RUNNER_TEST_MODE");
var resultDirectory = args[Array.IndexOf(args, "--results-directory") + 1];
if (args.Contains("--coverlet"))
{
    if (mode == "no-coverage") return 0;
    var projects = new[] { "Core", "Infrastructure", "Mcp", "Web" }
        .Where(project => mode != "no-mcp" || project != "Mcp")
        .Select(project => $"<package name='ActivityExplorer.{project}'><classes><class name='Covered' /></classes></package>");
    var line = mode == "below-line" ? "0.79" : "0.9";
    var branch = mode == "below-branch" ? "0.59" : "0.8";
    File.WriteAllText(Path.Combine(resultDirectory, "coverage.cobertura.test.xml"),
        $"<coverage line-rate='{line}' branch-rate='{branch}'><packages>{string.Concat(projects)}</packages></coverage>");
    return 0;
}

var scratch = Path.Combine(resultDirectory, "pressure-scratch");
File.WriteAllText(Path.Combine(scratch, "fixture.db"), "disposable");
File.WriteAllText(Path.Combine(scratch, "fixture.db-wal"), "disposable");
using var heldFile = mode == "wait"
    ? File.Open(Path.Combine(scratch, "fixture.db"), FileMode.Open, FileAccess.Read, FileShare.None)
    : null;
using var worker = Process.GetCurrentProcess();
var sample = Path.Combine(resultDirectory, "samples.json");
File.WriteAllText(sample + ".tmp", JsonSerializer.Serialize(new
{
    processId = worker.Id,
    startTimeUtcTicks = worker.StartTime.ToUniversalTime().Ticks
}));
File.Move(sample + ".tmp", sample);
Console.WriteLine($"Worker {Environment.ProcessId} started");
if (mode == "wait") Thread.Sleep(TimeSpan.FromSeconds(30));
return mode == "fail" ? 3 : 0;
'@ | Set-Content -LiteralPath (Join-Path $root.FullName 'Program.cs')
    $failureMessage = 'Could not build the synthetic verification worker.'
    dotnet build $project --nologo --disable-build-servers --output (Join-Path $root.FullName 'bin') `
        "-p:RestoreSources=$($root.FullName)" -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false *> $null
    if ($LASTEXITCODE -ne 0) { throw $failureMessage }
    Write-Output 'Synthetic verification worker built.'
    $driver = Join-Path $root.FullName 'bin/SyntheticWorker.dll'

    $failureMessage = 'Coverage verification failed.'
    $coverage = Join-Path $PSScriptRoot 'verify-coverage.ps1'
    $coverageDir = [IO.Directory]::CreateDirectory((Join-Path $root.FullName 'coverage')).FullName
    $coverageArgs = @{ ResultsDirectory = $coverageDir; TestAssembly = $driver }
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = 'complete'
    & $coverage @coverageArgs *> $null
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = 'no-coverage'
    Assert-RunnerFailure { & $coverage @coverageArgs *> $null } 'found 0'
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = 'no-mcp'
    Assert-RunnerFailure { & $coverage @coverageArgs *> $null } 'missing required projects: ActivityExplorer.Mcp'
    foreach ($mode in 'below-line', 'below-branch') {
        $env:AE_VERIFICATION_RUNNER_TEST_MODE = $mode
        Assert-RunnerFailure { & $coverage @coverageArgs *> $null } 'Coverage is below the required'
    }
    Write-Output 'Coverage passed report selection, required projects, and independent line and branch threshold cases.'

    $failureMessage = 'Could not prepare the pressure verification fixtures.'
    $runner = Join-Path $PSScriptRoot 'run-database-pressure.ps1'
    $metadata = [IO.Directory]::CreateDirectory((Join-Path $root.FullName 'metadata')).FullName
    $fixtures = [IO.Directory]::CreateDirectory((Join-Path $root.FullName 'fixtures')).FullName
    [IO.File]::WriteAllText((Join-Path $metadata 'history-100.json'), '{}')
    $shared = Join-Path $fixtures 'shared.db'
    [IO.File]::WriteAllText($shared, 'preserved')
    $arguments = @{
        Mode = 'Profile'; CandidateDll = $driver; Dimensions = @('history'); Scales = @(100)
        Blocks = 1; Scenarios = @('activities-owner'); Regimes = @('cold-solo')
        MetadataRoot = $metadata; FixtureRoot = $fixtures
    }
    foreach ($case in @(
        @{ Name = 'success'; Mode = 'complete'; Timeout = 10 },
        @{ Name = 'failure'; Mode = 'fail'; Timeout = 10 },
        @{ Name = 'timeout'; Mode = 'wait'; Timeout = 2 },
        @{ Name = 'interruption'; Mode = 'wait'; Timeout = 30 }
    )) {
        $failureMessage = "Pressure verification failed for the $($case.Name) case."
        $arguments.Output = Join-Path $root.FullName $case.Name
        $arguments.ProcessTimeoutSeconds = $case.Timeout
        $env:AE_VERIFICATION_RUNNER_TEST_MODE = $case.Mode
        $run = Join-Path $arguments.Output 'runs/baseline-history-100-0'
        $samples = Join-Path $run 'samples.json'
        $workerEvidence.Add($samples)
        if ($case.Name -eq 'success') {
            & $runner @arguments *> $null
            Assert-RunnerFailure { & $runner @arguments *> $null } 'Run already exists'
        } elseif ($case.Name -eq 'interruption') {
            $pipeline = [PowerShell]::Create()
            try {
                [void]$pipeline.AddCommand($runner).AddParameters($arguments)
                $pending = $pipeline.BeginInvoke()
                $timer = [Diagnostics.Stopwatch]::StartNew()
                while (-not [IO.File]::Exists($samples) -and -not $pending.IsCompleted -and $timer.Elapsed.TotalSeconds -lt 10) {
                    [Threading.Thread]::Sleep(50)
                }
                if (-not [IO.File]::Exists($samples)) { throw 'Synthetic worker did not reach the interruption point.' }
                $pipeline.Stop()
            }
            finally { $pipeline.Dispose() }
        } else {
            Assert-RunnerFailure { & $runner @arguments *> $null } 'Pressure process failed'
        }
        if (Test-Path -LiteralPath (Join-Path $run 'pressure-scratch')) { throw "$($case.Name) left pressure scratch." }
        foreach ($evidence in 'config.json', 'samples.json', 'stdout.log', 'stderr.log') {
            if (-not [IO.File]::Exists((Join-Path $run $evidence))) { throw "$($case.Name) lost $evidence." }
        }
        $worker = Get-OwnedWorker $samples
        if ($null -ne $worker) {
            try {
                if (-not $worker.WaitForExit(1000)) { throw "$($case.Name) left worker $($worker.Id) running." }
            } finally { $worker.Dispose() }
        }
        if ([IO.File]::ReadAllText($shared) -ne 'preserved') { throw "$($case.Name) changed the shared fixture." }
    }
    Write-Output 'Pressure cleanup passed success, failure, timeout, and controlled interruption.'
} catch {
    $failure = $failureMessage
}
finally {
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = $originalMode
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $originalTelemetry
    if ($null -ne $root) {
        $cleanupFailure = "Could not verify worker shutdown. Scratch retained in temp folder $($root.Name)."
        try {
            foreach ($sample in $workerEvidence) {
                $worker = Get-OwnedWorker $sample
                if ($null -eq $worker) { continue }
                try {
                    try { if (-not $worker.HasExited) { $worker.Kill($true) } }
                    catch [InvalidOperationException] { if (-not $worker.HasExited) { throw } }
                    if (-not $worker.WaitForExit(5000)) {
                        $cleanupFailure = "Could not stop worker $($worker.Id). Scratch retained in temp folder $($root.Name)."
                        throw $cleanupFailure
                    }
                } finally { $worker.Dispose() }
            }

            $cleanupFailure = "Cannot verify ownership of regression scratch folder $($root.Name)."
            if ($root.Parent.FullName -ne $temp) { throw $cleanupFailure }
            for ($ancestor = $root; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
                $ancestor.Refresh()
                if (-not $ancestor.Exists -or ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw $cleanupFailure
                }
            }

            $cleanupFailure = "Could not remove regression scratch folder $($root.Name)."
            $root.Delete($true)
            $root.Refresh()
            if ($root.Exists) { throw $cleanupFailure }
        } catch {
            $failure = $cleanupFailure
        }
    }
}

if ($null -ne $failure) {
    # PowerShell error records can serialize source paths even when the message is safe.
    [Console]::Error.WriteLine($failure)
    exit 1
}
