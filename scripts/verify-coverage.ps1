param(
    [string]$ResultsDirectory = "tests/ActivityExplorer.Tests/TestResults",
    [double]$MinimumLineRate = 0.80,
    [double]$MinimumBranchRate = 0.60
)

$report = Get-ChildItem -LiteralPath $ResultsDirectory -Filter "coverage*.cobertura*.xml" -Recurse |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $report) { throw "No Cobertura coverage report was found under $ResultsDirectory." }

[xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
$lineRate = [double]::Parse($coverage.coverage.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
$branchRate = [double]::Parse($coverage.coverage.'branch-rate', [Globalization.CultureInfo]::InvariantCulture)
Write-Host ("Coverage: {0:P2} line / {1:P2} branch" -f $lineRate, $branchRate)
if ($lineRate -lt $MinimumLineRate -or $branchRate -lt $MinimumBranchRate) {
    throw ("Coverage is below the required {0:P0} line / {1:P0} branch gate." -f $MinimumLineRate, $MinimumBranchRate)
}
