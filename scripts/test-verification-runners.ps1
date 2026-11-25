[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$temp = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$root = [IO.Directory]::CreateTempSubdirectory('activity-explorer-verification-')
$originalMode = $env:AE_VERIFICATION_RUNNER_TEST_MODE
$originalTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT

function Assert-RunnerFailure([scriptblock]$action, [string]$expected) {
    try { & $action }
    catch {
        if ($_.Exception.Message -like "*$expected*") { return }
        throw
    }
    throw "Expected the runner to fail with: $expected"
}

try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
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
var mode = Environment.GetEnvironmentVariable("AE_VERIFICATION_RUNNER_TEST_MODE");
var resultDirectory = args[Array.IndexOf(args, "--results-directory") + 1];
if (args.Contains("--coverlet"))
{
    if (mode == "no-coverage") return 0;
    var projects = new[] { "Core", "Infrastructure", "Mcp", "Web" }
        .Where(project => mode != "no-mcp" || project != "Mcp")
        .Select(project => $"<package name='ActivityExplorer.{project}'><classes><class name='Covered' /></classes></package>");
    File.WriteAllText(Path.Combine(resultDirectory, "coverage.cobertura.test.xml"),
        "<coverage line-rate='0.9' branch-rate='0.8'><packages>" + string.Concat(projects) + "</packages></coverage>");
    return 0;
}

var scratch = Path.Combine(resultDirectory, "pressure-scratch");
File.WriteAllText(Path.Combine(scratch, "fixture.db"), Environment.ProcessId.ToString());
File.WriteAllText(Path.Combine(scratch, "fixture.db-wal"), "disposable");
File.WriteAllText(Path.Combine(resultDirectory, "samples.json"), "{}");
Console.WriteLine($"Worker {Environment.ProcessId} started");
if (mode == "wait") Thread.Sleep(TimeSpan.FromSeconds(30));
return mode == "fail" ? 3 : 0;
'@ | Set-Content -LiteralPath (Join-Path $root.FullName 'Program.cs')
    dotnet build $project --nologo --disable-build-servers --output (Join-Path $root.FullName 'bin') "-p:RestoreSources=$($root.FullName)"
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the synthetic verification worker.' }
    $driver = Join-Path $root.FullName 'bin/SyntheticWorker.dll'

    $coverage = Join-Path $PSScriptRoot 'verify-coverage.ps1'
    $coverageDir = [IO.Directory]::CreateDirectory((Join-Path $root.FullName 'coverage')).FullName
    $coverageArgs = @{ ResultsDirectory = $coverageDir; TestAssembly = $driver }
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = 'complete'
    & $coverage @coverageArgs
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = 'no-coverage'
    Assert-RunnerFailure { & $coverage @coverageArgs } 'found 0'
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = 'no-mcp'
    Assert-RunnerFailure { & $coverage @coverageArgs } 'missing required projects: ActivityExplorer.Mcp'
    Write-Output 'Coverage passed current-report, stale-report, and missing-project cases.'

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
        $arguments.Output = Join-Path $root.FullName $case.Name
        $arguments.ProcessTimeoutSeconds = $case.Timeout
        $env:AE_VERIFICATION_RUNNER_TEST_MODE = $case.Mode
        $run = Join-Path $arguments.Output 'runs/baseline-history-100-0'
        if ($case.Name -eq 'success') {
            & $runner @arguments
            Assert-RunnerFailure { & $runner @arguments } 'Run already exists'
        } elseif ($case.Name -eq 'interruption') {
            $pipeline = [PowerShell]::Create()
            try {
                [void]$pipeline.AddCommand($runner).AddParameters($arguments)
                $pending = $pipeline.BeginInvoke()
                $timer = [Diagnostics.Stopwatch]::StartNew()
                $samples = Join-Path $run 'samples.json'
                while (-not [IO.File]::Exists($samples) -and -not $pending.IsCompleted -and $timer.Elapsed.TotalSeconds -lt 10) {
                    [Threading.Thread]::Sleep(50)
                }
                if (-not [IO.File]::Exists($samples)) { throw 'Synthetic worker did not reach the interruption point.' }
                $pipeline.Stop()
            }
            finally { $pipeline.Dispose() }
        } else {
            Assert-RunnerFailure { & $runner @arguments } 'Pressure process failed'
        }
        if (Test-Path -LiteralPath (Join-Path $run 'pressure-scratch')) { throw "$($case.Name) left pressure scratch." }
        foreach ($evidence in 'config.json', 'samples.json', 'stdout.log', 'stderr.log') {
            if (-not [IO.File]::Exists((Join-Path $run $evidence))) { throw "$($case.Name) lost $evidence." }
        }
        if ([IO.File]::ReadAllText($shared) -ne 'preserved') { throw "$($case.Name) changed the shared fixture." }
    }
    Write-Output 'Pressure cleanup passed success, failure, timeout, and controlled interruption.'
}
finally {
    $env:AE_VERIFICATION_RUNNER_TEST_MODE = $originalMode
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $originalTelemetry
    if ($root.Parent.FullName -ne $temp) {
        throw "Cannot verify owned regression scratch at $($root.FullName)."
    }
    for ($ancestor = $root; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        $ancestor.Refresh()
        if (-not $ancestor.Exists -or ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Cannot verify owned regression scratch at $($root.FullName)."
        }
    }
    $root.Delete($true)
    $root.Refresh()
    if ($root.Exists) { throw "Could not remove regression scratch at $($root.FullName)." }
}
