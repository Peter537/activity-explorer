[CmdletBinding()]
param(
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '../tests/ActivityExplorer.Tests/TestResults'),
    [string]$TestAssembly = (Join-Path $PSScriptRoot '../tests/ActivityExplorer.Tests/bin/Release/net10.0/ActivityExplorer.Tests.dll'),
    [ValidateRange(0.0, 1.0)][double]$MinimumLineRate = 0.80,
    [ValidateRange(0.0, 1.0)][double]$MinimumBranchRate = 0.60
)

$ErrorActionPreference = 'Stop'
$driver = Get-Item -LiteralPath $TestAssembly
if ($driver.PSIsContainer) { throw "Expected a built test assembly at $TestAssembly." }

$run = New-Item -ItemType Directory -Path (Join-Path $ResultsDirectory "coverage-$([Guid]::NewGuid().ToString('N'))")
try {
    dotnet exec $driver.FullName --coverlet --coverlet-output-format cobertura --results-directory $run.FullName
    if ($LASTEXITCODE -ne 0) { throw "Coverage test run failed with exit code $LASTEXITCODE. Results directory: $($run.FullName)." }

    $reports = @(Get-ChildItem -LiteralPath $run.FullName -Filter 'coverage*.cobertura*.xml' -File)
    if ($reports.Count -ne 1) { throw "Expected one Cobertura report from this coverage run, found $($reports.Count) under $($run.FullName)." }
    $report = $reports[0]
    Write-Host "Coverage report: $($report.FullName)"

    [xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
    $packages = @($coverage.SelectNodes('/coverage/packages/package[classes/class]') | ForEach-Object { $_.GetAttribute('name') })
    $required = @('ActivityExplorer.Core', 'ActivityExplorer.Infrastructure', 'ActivityExplorer.Mcp', 'ActivityExplorer.Web')
    $missing = @($required | Where-Object { $_ -cnotin $packages })
    if ($missing.Count) { throw "Coverage is missing required projects: $($missing -join ', ')." }

    $lineRate = [double]::Parse($coverage.coverage.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
    $branchRate = [double]::Parse($coverage.coverage.'branch-rate', [Globalization.CultureInfo]::InvariantCulture)
    if (-not [double]::IsFinite($lineRate) -or -not [double]::IsFinite($branchRate) -or
        $lineRate -lt 0 -or $lineRate -gt 1 -or $branchRate -lt 0 -or $branchRate -gt 1) {
        throw 'Coverage rates must be finite values between zero and one.'
    }
    Write-Host ("Coverage: {0:P2} line / {1:P2} branch" -f $lineRate, $branchRate)
    if ($lineRate -lt $MinimumLineRate -or $branchRate -lt $MinimumBranchRate) {
        throw ("Coverage is below the required {0:P0} line / {1:P0} branch gate." -f $MinimumLineRate, $MinimumBranchRate)
    }
}
finally {
    if ([IO.Directory]::GetFileSystemEntries($run.FullName).Length -eq 0) {
        $run.Delete()
    }
}
