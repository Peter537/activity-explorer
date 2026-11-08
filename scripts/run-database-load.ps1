[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Profile', 'Compare')][string]$Mode = 'Profile',
    [string]$Output = 'artifacts/database-load',
    [string]$FixtureRoot,
    [int[]]$Scales = @(1, 10, 100, 1000),
    [ValidateSet('history', 'profiles')][string[]]$Dimensions = @('history', 'profiles'),
    [string]$BaselineDll,
    [string]$CandidateDll = 'tests/ActivityExplorer.Tests/bin/Release/net10.0/ActivityExplorer.Tests.dll',
    [ValidateSet('baseline', 'candidate')][string]$ProfileVariant = 'baseline',
    [int]$Blocks = 16,
    [int]$BudgetMs = 5000,
    [int]$Warmups = 2,
    [int]$Repetitions = 1,
    [int]$Seed = 5372026,
    [int]$ProcessTimeoutSeconds = 180,
    [string[]]$Scenarios = @(),
    [switch]$Diagnostic
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    if ($Blocks -lt 1 -or $BudgetMs -lt 1 -or $Warmups -lt 0 -or $Repetitions -lt 1 -or $ProcessTimeoutSeconds -lt 1 -or ($Scales | Where-Object { $_ -notin @(1,10,100,1000) })) { throw 'Use positive blocks/budget/process timeout/repetitions, nonnegative warmups, and scales 1, 10, 100, 1000.' }
    $outputRoot = [IO.Path]::GetFullPath($Output)
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $fixtureRootPath = if ($FixtureRoot) { [IO.Path]::GetFullPath($FixtureRoot) } else { Join-Path $outputRoot 'fixtures' }
    $candidate = [IO.Path]::GetFullPath($CandidateDll)
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'Build the Release test executable first.' }
    if ($Mode -eq 'Compare') {
        if (-not $BaselineDll) { throw 'Compare requires -BaselineDll pointing to an immutable copy of the complete baseline test output.' }
        $baseline = [IO.Path]::GetFullPath($BaselineDll)
        if (-not (Test-Path -LiteralPath $baseline -PathType Leaf)) { throw 'Baseline DLL does not exist.' }
        if ($baseline -eq $candidate) { throw 'Baseline and candidate must be separate build outputs.' }
        $plan = @{
            Blocks = $Blocks; Scales = $Scales; Dimensions = $Dimensions; Scenarios = $Scenarios; FixtureRoot = $fixtureRootPath
            BudgetMs = $BudgetMs; Warmups = $Warmups; Repetitions = $Repetitions; Order = 'Independent seeded fair AB/BA assignment per block'; Seed = $Seed
            BaselineAssemblyHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $baseline) 'ActivityExplorer.Infrastructure.dll')).Hash
            CandidateAssemblyHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $candidate) 'ActivityExplorer.Infrastructure.dll')).Hash
        }
        [IO.Directory]::CreateDirectory((Join-Path $outputRoot 'runs')) | Out-Null
        $planName = if ($Diagnostic) { 'diagnostic-campaign-plan.json' } else { 'campaign-plan.json' }
        $planPath = Join-Path $outputRoot "runs/$planName"
        $planBytes = [Text.Encoding]::UTF8.GetBytes(($plan | ConvertTo-Json -Depth 8))
        $planStream = [IO.FileStream]::new($planPath, [IO.FileMode]::CreateNew)
        try { $planStream.Write($planBytes, 0, $planBytes.Length) } finally { $planStream.Dispose() }
    }
    function Invoke-Workload([string]$dll, [hashtable]$config, [string]$name, [int]$timeoutSeconds) {
        $runDir = Join-Path $outputRoot ('runs/' + $name)
        if (Test-Path -LiteralPath $runDir) { throw "Run already exists: $runDir. Choose a new output directory or blocks." }
        [IO.Directory]::CreateDirectory($runDir) | Out-Null
        $config.ReportFile = Join-Path $runDir 'samples.json'
        $configFile = Join-Path $runDir 'config.json'
        $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configFile -Encoding utf8NoBOM
        $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.WorkingDirectory = $repo
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('exec', $dll, '--filter-class', 'ActivityExplorer.Tests.DatabaseLoadTests', '--results-directory', $runDir)) { $start.ArgumentList.Add($argument) }
        $start.Environment['AE_DATABASE_LOAD_CONFIG'] = $configFile
        $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($timeoutSeconds * 1000)
        if (-not $finished) { $process.Kill($true); $process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $runDir 'stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $runDir 'stderr.log'), $stderr.GetAwaiter().GetResult())
        if (-not $finished -or $process.ExitCode -ne 0) { throw "Workload process failed or exceeded ${timeoutSeconds}s: $name. See $runDir; partial samples retained." }
        if (-not (Test-Path -LiteralPath $config.ReportFile)) { throw "No workload report: $name" }
        Write-Output $name
    }
    $random = [Random]::new($Seed)
    foreach ($dimension in $Dimensions) {
        $dimension = $dimension.ToLowerInvariant()
        foreach ($scale in $Scales) {
            $fixture = Join-Path $fixtureRootPath "$dimension-$scale"
            $baseConfig = @{ FixtureDirectory = $fixture; Scale = $scale; Dimension = $dimension; BudgetMs = $BudgetMs; Scenarios = $Scenarios; Warmups = $Warmups; Repetitions = $Repetitions; Diagnostic = [bool]$Diagnostic }
            if ($Mode -eq 'Prepare') {
                $config = $baseConfig.Clone(); $config.Prepare = $true
                Invoke-Workload $candidate $config "prepare-$dimension-$scale" 1200
                continue
            }
            if (-not (Test-Path -LiteralPath (Join-Path $fixture "load-$dimension-$scale.db.manifest"))) { throw 'Prepare fixtures first using -Mode Prepare with the same output/scales/dimensions.' }
            for ($block = 0; $block -lt $Blocks; $block++) {
                $variants = if ($Mode -eq 'Compare') {
                    # Independent assignment per block avoids correlated forced-reverse pairs.
                    if ($random.Next(2) -eq 1) { @('candidate','baseline') } else { @('baseline','candidate') }
                } else { @($ProfileVariant) }
                foreach ($variant in $variants) {
                    $config = $baseConfig.Clone(); $config.Block = $block
                    $config.Variant = if ($Diagnostic) { 'diagnostic-' + $variant } else { $variant }
                    $dll = if ($variant -eq 'baseline' -and $Mode -eq 'Compare') { $baseline } else { $candidate }
                    Invoke-Workload $dll $config "$($config.Variant)-$dimension-$scale-$block" $ProcessTimeoutSeconds
                }
            }
        }
    }
} finally { Pop-Location }
