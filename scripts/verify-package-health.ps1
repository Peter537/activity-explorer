param(
    [string]$Solution = "ActivityExplorer.slnx",
    [string]$VendorDirectory = "src/ActivityExplorer.Web/wwwroot/vendor"
)

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
Test-Report @("--deprecated", "--include-transitive") "deprecationReasons" "deprecated"

function Test-MapLibreProvenance([string]$Directory) {
    $provenancePath = Join-Path $Directory "MAPLIBRE-PROVENANCE.json"
    if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
        throw "MapLibre provenance is missing at $provenancePath."
    }

    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.package -ne "maplibre-gl" -or [string]::IsNullOrWhiteSpace($provenance.version)) {
        throw "MapLibre provenance has an unexpected package identity or version."
    }

    $expectedSource = "https://registry.npmjs.org/maplibre-gl/-/maplibre-gl-$($provenance.version).tgz"
    if ($provenance.source -ne $expectedSource -or $provenance.npmIntegrity -notmatch '^sha512-[A-Za-z0-9+/]+={0,2}$') {
        throw "MapLibre provenance has an unexpected source or npm integrity value."
    }
    if ($provenance.tarballSha512 -notmatch '^[A-Fa-f0-9]{128}$') {
        throw "MapLibre provenance has an invalid tarball SHA-512 value."
    }

    $requiredFiles = @(
        "maplibre-gl.css",
        "maplibre-gl.mjs",
        "maplibre-gl.mjs.map",
        "maplibre-gl-shared.mjs",
        "maplibre-gl-shared.mjs.map",
        "maplibre-gl-worker.mjs",
        "maplibre-gl-worker.mjs.map"
    )
    $files = @($provenance.filesSha256.PSObject.Properties)

    $recordedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) {
        if ([IO.Path]::GetFileName($file.Name) -ne $file.Name -or $file.Value -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "MapLibre provenance contains an invalid file name or SHA-256 for $($file.Name)."
        }
        [void]$recordedNames.Add($file.Name)
        $filePath = Join-Path $Directory $file.Name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Vendored MapLibre file $($file.Name) is missing."
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -ne $file.Value) {
            throw "Vendored MapLibre file $($file.Name) does not match its recorded SHA-256."
        }
    }

    $missingRecords = $requiredFiles | Where-Object { -not $recordedNames.Contains($_) }
    $unexpectedRecords = $recordedNames | Where-Object { $_ -notin $requiredFiles }
    if ($missingRecords -or $unexpectedRecords) {
        throw "MapLibre provenance does not contain the exact required runtime asset set. Missing: $(@($missingRecords) -join ', '); unexpected: $(@($unexpectedRecords) -join ', ')."
    }

    $licensePath = Join-Path $Directory $provenance.licenseFile
    if ($provenance.licenseFile -ne "MAPLIBRE-LICENSE.txt" -or -not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
        throw "The recorded MapLibre license file is missing."
    }

    $unlistedFiles = Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Name -notin @("MAPLIBRE-PROVENANCE.json", $provenance.licenseFile) -and -not $recordedNames.Contains($_.Name) }
    if ($unlistedFiles) {
        throw "Found unlisted vendored MapLibre file(s): $(@($unlistedFiles.Name) -join ', ')."
    }

    Write-Host "Verified $($files.Count) vendored MapLibre $($provenance.version) file hash(es)."
}

Test-MapLibreProvenance $VendorDirectory
