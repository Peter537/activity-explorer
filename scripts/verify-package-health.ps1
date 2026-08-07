param([string]$Solution = "ActivityExplorer.slnx")

function Test-Report([string[]]$Arguments, [string]$FindingProperty, [string]$Label) {
    $json = & dotnet package list --project $Solution @Arguments --format json --output-version 1 --no-restore
    if ($LASTEXITCODE -ne 0) { throw "The $Label package check could not run." }
    $document = $json | ConvertFrom-Json
    $script:PackageFindings = 0
    function Visit($value) {
        if ($null -eq $value) { return }
        if ($value -is [System.Collections.IEnumerable] -and $value -isnot [string]) {
            foreach ($item in $value) { Visit $item }
            return
        }
        if ($value -is [psobject]) {
            foreach ($property in $value.PSObject.Properties) {
                if ($property.Name -eq $FindingProperty -and $null -ne $property.Value -and @($property.Value).Count -gt 0) {
                    $script:PackageFindings += @($property.Value).Count
                }
                Visit $property.Value
            }
        }
    }
    Visit $document
    if ($script:PackageFindings -gt 0) { throw "Found $script:PackageFindings $Label package finding(s)." }
    Write-Host "No $Label package findings."
}

Test-Report @("--vulnerable", "--include-transitive") "vulnerabilities" "vulnerable"
Test-Report @("--deprecated") "deprecationReasons" "deprecated direct"
