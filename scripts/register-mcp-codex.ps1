#requires -Version 7.0
<#
.SYNOPSIS
Registers the published Activity Explorer MCP server with local Codex clients. Defaults to read-only.
.DESCRIPTION
Updates only the named server in Codex's user configuration. Does not build the
server, initialize application data, or write machine-specific paths to the repo.
.PARAMETER DataDirectory
Existing data directory used by the web application, containing activity-explorer.db.
.PARAMETER PublishedDirectory
Published server directory. Defaults to artifacts/mcp in this checkout.
.EXAMPLE
./scripts/register-mcp-codex.ps1 -DataDirectory $env:ACTIVITY_EXPLORER_DATA -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$DataDirectory,

    [string]$PublishedDirectory = (Join-Path $PSScriptRoot '../artifacts/mcp'),

    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$ServerName = 'activity-explorer',

    [string]$CodexCommand = 'codex',
    [string]$DotnetCommand = 'dotnet',
    [switch]$EnableWrites,
    [Guid[]]$WriteProfileIds,
    [switch]$AllowDestructive,
    [switch]$AllowAdministration
)

$ErrorActionPreference = 'Stop'
if ($EnableWrites -and (-not $WriteProfileIds -or $WriteProfileIds.Contains([Guid]::Empty))) {
    throw 'EnableWrites requires explicit nonempty WriteProfileIds.'
}
if (-not $EnableWrites -and ($WriteProfileIds -or $AllowDestructive -or $AllowAdministration)) {
    throw 'Write permissions require EnableWrites. The default remains read-only.'
}

if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) {
    throw 'DataDirectory must be an existing directory used by the web application.'
}
$dataRoot = (Resolve-Path -LiteralPath $DataDirectory).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'activity-explorer.db') -PathType Leaf)) {
    throw 'DataDirectory has no activity-explorer.db. Initialize or import through the web application first.'
}

foreach ($file in @('ActivityExplorer.Mcp.dll', 'ActivityExplorer.Mcp.deps.json', 'ActivityExplorer.Mcp.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory $file) -PathType Leaf)) {
        throw "Published server file $file is missing. Publish ActivityExplorer.Mcp as described in docs/mcp.md first."
    }
}
$serverPath = (Resolve-Path -LiteralPath (Join-Path $PublishedDirectory 'ActivityExplorer.Mcp.dll')).ProviderPath
$dotnet = Get-Command $DotnetCommand -CommandType Application -ErrorAction Stop
$codex = Get-Command $CodexCommand -CommandType Application, ExternalScript -ErrorAction Stop
$runtimes = & $dotnet.Source --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft.NETCore.App 10\.')) {
    throw 'The .NET 10 runtime is required. Check the selected dotnet installation.'
}

if ($PSCmdlet.ShouldProcess('Codex user MCP configuration', "Register '$ServerName' using the published DLL and selected data directory")) {
    $codexArguments = @(
        'mcp', 'add', $ServerName,
        '--env', "ACTIVITY_EXPLORER_DATA=$dataRoot",
        '--env', "Mcp__AccessMode=$(if ($EnableWrites) { 'ReadWrite' } else { 'ReadOnly' })"
    )
    if ($EnableWrites) {
        for ($profileIndex = 0; $profileIndex -lt $WriteProfileIds.Count; $profileIndex++) {
            $codexArguments += @('--env', "Mcp__AllowedWriteProfileIds__${profileIndex}=$($WriteProfileIds[$profileIndex])")
        }
        $codexArguments += @('--env', "Mcp__AllowDestructive=$($AllowDestructive.IsPresent)", '--env', "Mcp__AllowAdministration=$($AllowAdministration.IsPresent)")
    }
    $codexArguments += @('--', $dotnet.Source, $serverPath)
    & $codex.Source @codexArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Codex registration failed with exit code $LASTEXITCODE."
    }
    Write-Host "Registered '$ServerName'. Restart the desktop app or IDE extension, or start a new CLI session."
    Write-Host 'Verify get_server_status and list_profiles as described in docs/mcp.md.'
}
