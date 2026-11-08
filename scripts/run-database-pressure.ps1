[CmdletBinding()]
param(
    [ValidateSet('Prepare','Profile','Compare')][string]$Mode='Profile',
    [string]$Output='artifacts/database-pressure',
    [string]$FixtureRoot='artifacts/database-load-results/fixtures',
    [string]$MetadataRoot='artifacts/database-pressure-metadata',
    [string]$CandidateFixtureRoot,
    [string]$CandidateMetadataRoot,
    [Nullable[int]]$CandidateDerivedSummaryVersion,
    [Nullable[int]]$CandidateExplorationCellSetVersion,
    [Nullable[int]]$CandidateExplorationAggregateVersion,
    [Nullable[int]]$CandidatePeriodRecordRowVersion,
    [switch]$UpgradeReadIndexes,
    [switch]$PrepareRecordEffortSummaries,
    [switch]$PrepareExplorationCellSets,
    [switch]$PrimeClone,
    [string[]]$RuntimeMetadataFiles=@(),
    [string]$CandidateDll='tests/ActivityExplorer.Tests/bin/Release/net10.0/ActivityExplorer.Tests.dll',
    [string]$BaselineDll,
    [ValidateSet('baseline','candidate')][string]$ProfileVariant='baseline',
    [ValidateSet('history','profiles')][string[]]$Dimensions=@('history','profiles'),
    [int[]]$Scales=@(100,1000),
    [string[]]$Scenarios=@(
        'activities-owner','activities-all','activities-deep-page','activities-text','activities-metric',
        'activities-tag','activities-import','activity-detail','filter-options','sport-counts',
        'imports-owner','imports-all','routes-library','segments-library','segment-efforts',
        'dashboard','calendar','records-persisted','records-attempts','records-history','records-period-streams',
        'exploration','map-activities','mixed'
    ),
    [ValidateSet('cold-solo','warm-burst','cold-burst')][string[]]$Regimes=@('cold-burst'),
    [int]$ScenariosPerProcess=4,
    [int]$Blocks=16,
    [int]$BudgetMs=2000,
    [int]$Warmups=2,
    [int]$Seed=5372028,
    [ValidateSet('Server','Workstation')][string]$GcMode='Server',
    [ValidateSet('Shared','Private')][string]$SqliteCacheMode='Shared',
    [ValidateSet('Shared','Private')][string]$CandidateSqliteCacheMode,
    [Nullable[long]]$MmapBytes,
    [hashtable]$CandidateMmapLimitsByRole=@{},
    [switch]$Diagnostic,
    [switch]$QuiescentHost,
    [switch]$ValidateManifests,
    [switch]$Resume,
    [switch]$StopOnCandidateFailure,
    [int]$ProcessTimeoutSeconds=600
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $allowedScales=if($Mode -eq 'Prepare'){@(1,100,1000)}else{@(100,1000)}
    if($Blocks -lt 1 -or $BudgetMs -lt 1 -or $Warmups -lt 0 -or $ProcessTimeoutSeconds -lt 1 -or ($Scales | Where-Object {$_ -notin $allowedScales})){throw 'Use positive blocks/budget/timeout, nonnegative warmups and scales100/1000 (also1 for preparation).'}
    if($Mode -ne 'Prepare' -and ($UpgradeReadIndexes -or $PrepareRecordEffortSummaries -or $PrepareExplorationCellSets -or $PrimeClone)){
        throw 'Fixture changes are only available in Prepare mode.'
    }
    if($null -ne $CandidateDerivedSummaryVersion -and $CandidateDerivedSummaryVersion -lt 1){
        throw 'Candidate derived summary version must be positive.'
    }
    if($null -ne $CandidateExplorationCellSetVersion -and $CandidateExplorationCellSetVersion -lt 1){
        throw 'Candidate exploration cell-set version must be positive.'
    }
    if($null -ne $CandidateExplorationAggregateVersion -and $CandidateExplorationAggregateVersion -lt 1){
        throw 'Candidate exploration aggregate version must be positive.'
    }
    if($null -ne $CandidatePeriodRecordRowVersion -and $CandidatePeriodRecordRowVersion -lt 1){
        throw 'Candidate period record row version must be positive.'
    }
    if($Diagnostic -and $Mode -ne 'Profile'){
        throw 'Reader diagnostics are only available in exploratory Profile runs.'
    }
    if($QuiescentHost -and ($Mode -eq 'Prepare' -or 'warm-burst' -in $Regimes)){
        throw 'Host readiness gating is available for cold Profile and Compare runs.'
    }
    $hostGatePolicy=if($QuiescentHost){
        [ordered]@{pollMs=500;consecutiveSamples=3;maxCurrentDiskQueue=1;maxAverageDiskQueue=1.5;maxCpuPercent=60;maxWaitMs=300000}
    }
    if($ValidateManifests -and $Mode -ne 'Compare'){
        throw 'Manifest validation requires a comparison plan.'
    }
    if($Resume -and $Mode -ne 'Compare'){
        throw 'Only a frozen paired comparison can be resumed.'
    }
    if($CandidateSqliteCacheMode -and $Mode -ne 'Compare'){
        throw 'Use SqliteCacheMode for single-variant profiles.'
    }
    if($CandidateMmapLimitsByRole.Count -and ($Mode -ne 'Compare' -or $null -ne $MmapBytes)){
        throw 'Per-role production mapping limits require Compare mode without a diagnostic override.'
    }
    if($ScenariosPerProcess -lt 0 -or $Scenarios.Count -eq 0 -or @($Scenarios | Sort-Object -Unique).Count -ne $Scenarios.Count){
        throw 'Provide distinct scenarios and a nonnegative process batch size.'
    }
    $batchWidth=if($ScenariosPerProcess){$ScenariosPerProcess}else{$Scenarios.Count}
    $scenarioBatches=[Collections.Generic.List[object]]::new()
    for($first=0;$first -lt $Scenarios.Count;$first+=$batchWidth){
        $last=[Math]::Min($first+$batchWidth,$Scenarios.Count)-1
        $scenarioBatches.Add([string[]]$Scenarios[$first..$last])
    }
    $candidateCache=if($CandidateSqliteCacheMode){$CandidateSqliteCacheMode}else{$SqliteCacheMode}
    $out=[IO.Path]::GetFullPath($Output)
    $fixtures=[IO.Path]::GetFullPath($FixtureRoot)
    $metadata=[IO.Path]::GetFullPath($MetadataRoot)
    $candidateFixtures=if($CandidateFixtureRoot){[IO.Path]::GetFullPath($CandidateFixtureRoot)}else{$fixtures}
    $candidateMetadata=if($CandidateMetadataRoot){[IO.Path]::GetFullPath($CandidateMetadataRoot)}else{$metadata}
    $candidate=[IO.Path]::GetFullPath($CandidateDll)
    if(-not(Test-Path -LiteralPath $candidate -PathType Leaf)){throw 'Build the Release driver first.'}
    [IO.Directory]::CreateDirectory($out)|Out-Null
    [IO.Directory]::CreateDirectory($metadata)|Out-Null
    if($Mode -eq 'Compare'){
        if(-not $BaselineDll){throw 'Compare requires a frozen baseline driver.'}
        $baseline=[IO.Path]::GetFullPath($BaselineDll)
        if($baseline -eq $candidate){throw 'Use separate immutable binary directories.'}
        $driverHash=(Get-FileHash -LiteralPath $candidate).Hash
        if((Get-FileHash -LiteralPath $baseline).Hash -ne $driverHash){throw 'Use byte-identical measurement driver DLLs with separate production assemblies.'}
        $plan=[ordered]@{
            Dimensions=$Dimensions;Scales=$Scales;Scenarios=$Scenarios;Regimes=$Regimes;Blocks=$Blocks;Concurrency=4
            ScenarioBatches=$scenarioBatches.ToArray()
            BudgetMs=$BudgetMs;Warmups=$Warmups;Seed=$Seed;FixtureRoot=$fixtures;MetadataRoot=$metadata
            CandidateFixtureRoot=$candidateFixtures;CandidateMetadataRoot=$candidateMetadata;GcMode=$GcMode
            BaselineFixtureRoot=$fixtures;BaselineMetadataRoot=$metadata
            CandidateDerivedSummaryVersion=$CandidateDerivedSummaryVersion;BaselineDerivedSummaryVersion=$null
            CandidateExplorationCellSetVersion=$CandidateExplorationCellSetVersion;BaselineExplorationCellSetVersion=$null
            CandidateExplorationAggregateVersion=$CandidateExplorationAggregateVersion
            CandidatePeriodRecordRowVersion=$CandidatePeriodRecordRowVersion
            StopOnCandidateFailure=[bool]$StopOnCandidateFailure
            SqliteCacheMode=$SqliteCacheMode;MmapBytes=$MmapBytes
            MmapProductionLimit=0;CandidateMmapLimitsByRole=$CandidateMmapLimitsByRole
            BaselineSqliteCacheMode=$SqliteCacheMode;CandidateSqliteCacheMode=$candidateCache
            ColdCopyWorkers=2;ColdCopySettleMs=5000
            HostGate=$hostGatePolicy
            RuntimeMetadataFiles=@($RuntimeMetadataFiles|ForEach-Object{[IO.Path]::GetFullPath($_)});RuntimePrimeIterations=2;RuntimePrimeConcurrency=4;RuntimePrimeSqliteCacheMode='Private';WarmupBudgetMs=60000
            Observation='Independent process-block burst tail (maximum request latency); every client must succeed strictly below budget including queueing.'
            Order='Seeded AB/BA per fixture and block, repeated for each adjacent scenario batch pair'
            Cache='Cold replicas staged before collection and verified afterward; runtime exercised on separate fixtures, no target read before its arrival; device/background caches uncontrolled'
            DriverHash=$driverHash
            BaselineAssemblyHash=(Get-FileHash -LiteralPath (Join-Path (Split-Path $baseline) 'ActivityExplorer.Infrastructure.dll')).Hash
            CandidateAssemblyHash=(Get-FileHash -LiteralPath (Join-Path (Split-Path $candidate) 'ActivityExplorer.Infrastructure.dll')).Hash
        }
        $runRoot=Join-Path $out 'runs'
        [IO.Directory]::CreateDirectory($runRoot)|Out-Null
        $planPath=Join-Path $runRoot 'campaign-plan.json'
        $planText=$plan|ConvertTo-Json -Depth 8
        if($Resume){
            $old=[Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($planPath))
            $requested=[Text.Json.Nodes.JsonNode]::Parse($planText)
            if(-not [Text.Json.Nodes.JsonNode]::DeepEquals($old,$requested)){throw 'Resume options or frozen binaries differ from the original comparison plan.'}

            $existing=@(Get-ChildItem -LiteralPath $runRoot -Directory)
            $orderedNames=[Collections.Generic.List[string]]::new()
            $sequenceRandom=[Random]::new($Seed)
            foreach($dimension in $Dimensions){foreach($scale in $Scales){for($block=0;$block -lt $Blocks;$block++){
                $order=if($sequenceRandom.Next(2) -eq 1){@('candidate','baseline')}else{@('baseline','candidate')}
                for($batch=0;$batch -lt $scenarioBatches.Count;$batch++){foreach($variant in $order){
                    $name="$variant-$dimension-$scale-$block"
                    if($scenarioBatches.Count -gt 1){$name+="-batch-$batch"}
                    $orderedNames.Add($name)
                }}
            }}}
            if($existing.Count -gt $orderedNames.Count){throw 'Extra run directories in the comparison output.'}
            for($i=0;$i -lt $existing.Count;$i++){
                if($orderedNames[$i] -cnotin $existing.Name){throw "Comparison files are not a contiguous seeded prefix at $($orderedNames[$i])."}
            }
            Write-Output "Resuming after $($existing.Count) retained process directories."
        }
        else{
            $planStream=[IO.FileStream]::new($planPath,[IO.FileMode]::CreateNew)
            try{$bytes=[Text.Encoding]::UTF8.GetBytes($planText);$planStream.Write($bytes,0,$bytes.Length)}finally{$planStream.Dispose()}
        }
        if($ValidateManifests){
            python -B scripts/analyze-database-load.py --preflight-plan $planPath
            if($LASTEXITCODE){throw 'Pressure manifest preflight failed.'}
        }
    }
    function Invoke-Pressure([string]$dll,[hashtable]$config,[string]$name){
        $run=Join-Path $out ('runs/'+$name)
        $config.ReportFile=Join-Path $run 'samples.json'
        $configFile=Join-Path $run 'config.json'
        $configText=$config|ConvertTo-Json -Depth 8
        if(Test-Path -LiteralPath $run){
            if(-not $Resume){throw "Run already exists: $run"}
            $previous=[Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($configFile))
            $requested=[Text.Json.Nodes.JsonNode]::Parse($configText)
            if(-not [Text.Json.Nodes.JsonNode]::DeepEquals($previous,$requested)){throw "Retained process configuration differs from plan: $name"}
            $document=Get-Content -LiteralPath $config.ReportFile -Raw | ConvertFrom-Json
            $expected=@(foreach($regime in $config.Regimes){foreach($scenario in $config.Scenarios){
                if($scenario -ne 'mixed' -or $regime -ne 'cold-solo'){"$scenario.$regime"}
            }})
            $expectedHash=if($config.Variant -eq 'baseline'){$plan.BaselineAssemblyHash}else{$plan.CandidateAssemblyHash}
            if(($expected -join '|') -cne (@($document.samples.scenario) -join '|') -or
                $document.metadata.driverHash -cne $plan.DriverHash -or $document.metadata.productionHash -cne $expectedHash -or
                @($document.metadata.copyChecks | Where-Object {$_.matches -eq $true}).Count -ne $expected.Count -or
                ($StopOnCandidateFailure -and $config.Variant -eq 'candidate' -and @($document.samples|Where-Object {$_.status -ne 'ok'}).Count) -or
                (Get-Content -LiteralPath (Join-Path $run 'stdout.log') -Raw) -notmatch 'Test run summary: Passed!'){
                throw "Retained process did not finish its planned measurement and verification: $name"
            }
            return
        }
        [IO.Directory]::CreateDirectory($run)|Out-Null
        $configText|Set-Content -LiteralPath $configFile -Encoding utf8NoBOM
        $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.WorkingDirectory=$repo;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        foreach($arg in @('exec',$dll,'--filter-class','ActivityExplorer.Tests.DatabasePressureLoadTests','--filter-method','*Run_configured_pressure_workload','--results-directory',$run)){$start.ArgumentList.Add($arg)}
        $start.Environment['AE_DATABASE_PRESSURE_CONFIG']=$configFile
        $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
        $start.Environment['DOTNET_gcServer']=if($GcMode -eq 'Server'){'1'}else{'0'}
        $process=[Diagnostics.Process]::Start($start)
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $finished=$process.WaitForExit($ProcessTimeoutSeconds*1000)
        if(-not $finished){$process.Kill($true);$process.WaitForExit()}
        [IO.File]::WriteAllText((Join-Path $run 'stdout.log'),$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $run 'stderr.log'),$stderr.GetAwaiter().GetResult())
        if(-not $finished -or $process.ExitCode -ne 0){throw "Pressure process failed: $name. Partial evidence retained in $run"}
        if(-not $config.SetupOnly -and -not(Test-Path -LiteralPath $config.ReportFile)){throw "Missing pressure samples: $name"}
        Write-Output $name
        if($StopOnCandidateFailure -and -not $config.SetupOnly -and $config.Variant -eq 'candidate'){
            $document=Get-Content -LiteralPath $config.ReportFile -Raw | ConvertFrom-Json
            $failed=@($document.samples | Where-Object {$_.status -ne 'ok'})
            if($failed.Count){
                $cases=($failed | ForEach-Object {$_.scenario}) -join ', '
                throw "Candidate exceeded the acceptance gate in $name ($cases). Evidence retained in $run"
            }
        }
    }
    $random=[Random]::new($Seed)
    foreach($dimension in $Dimensions){foreach($scale in $Scales){
        $manifest=Join-Path $metadata "$dimension-$scale.json"
        $config=@{FixtureDirectory=(Join-Path $fixtures "$dimension-$scale");RuntimeFixtureDirectory=(Join-Path $fixtures "$dimension-1");MetadataFile=$manifest;Scale=$scale;Dimension=$dimension;BudgetMs=$BudgetMs;Warmups=$Warmups;Regimes=$Regimes;Scenarios=$Scenarios;CacheMode=$SqliteCacheMode}
        if($null -ne $MmapBytes){$config.MmapBytes=[long]$MmapBytes}
        if($hostGatePolicy){$config.HostGate=$hostGatePolicy}
        if($Diagnostic){$config.Diagnostic=$true}
        if($RuntimeMetadataFiles.Count){$config.RuntimeMetadataFiles=@($RuntimeMetadataFiles|ForEach-Object{[IO.Path]::GetFullPath($_)})}
        if($Mode -eq 'Prepare'){
            if(Test-Path -LiteralPath $manifest){throw 'Metadata exists; choose a new metadata root for revalidation.'}
            $setup=$config.Clone();$setup.SetupOnly=$true
            if($PrimeClone){$setup.PrimeClone=$true}
            if($UpgradeReadIndexes){$setup.UpgradeReadIndexes=$true}
            if($PrepareRecordEffortSummaries){$setup.PrepareRecordEffortSummaries=$true}
            if($PrepareExplorationCellSets){$setup.PrepareExplorationCellSets=$true}
            Invoke-Pressure $candidate $setup "prepare-$dimension-$scale"
            continue
        }
        if(-not(Test-Path -LiteralPath $manifest)){throw 'Prepare and validate fixture metadata first.'}
        for($block=0;$block -lt $Blocks;$block++){
            $variants=if($Mode -eq 'Compare'){if($random.Next(2) -eq 1){@('candidate','baseline')}else{@('baseline','candidate')}}else{@($ProfileVariant)}
            for($batch=0;$batch -lt $scenarioBatches.Count;$batch++){
                foreach($variant in $variants){
                    $trial=$config.Clone();$trial.Variant=$variant;$trial.Block=$block
                    $trial.Scenarios=$scenarioBatches[$batch]
                    if($Mode -eq 'Compare' -and $variant -eq 'candidate'){
                        $trial.FixtureDirectory=Join-Path $candidateFixtures "$dimension-$scale"
                        $trial.MetadataFile=Join-Path $candidateMetadata "$dimension-$scale.json"
                        $trial.RuntimeFixtureDirectory=Join-Path $candidateFixtures "$dimension-1"
                        $trial.CacheMode=$candidateCache
                        if(-not(Test-Path -LiteralPath $trial.MetadataFile)){throw 'Prepare candidate fixture metadata before comparison.'}
                    }
                    $dll=if($variant -eq 'baseline' -and $Mode -eq 'Compare'){$baseline}else{$candidate}
                    $name="$variant-$dimension-$scale-$block"
                    if($scenarioBatches.Count -gt 1){$name+="-batch-$batch"}
                    Invoke-Pressure $dll $trial $name
                }
            }
        }
    }}
}finally{Pop-Location}
