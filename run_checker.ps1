<#
.SYNOPSIS
    Fast concurrent M3U link validator.

.DESCRIPTION
    Checks all stream URLs in an M3U file concurrently and outputs only the
    working channels to a new M3U playlist file while preserving all metadata.

.PARAMETER InputFile
    Path to the source M3U file. Default: "index.m3u"

.PARAMETER OutputFile
    Path to write working channels. Default: "index1.m3u"

.PARAMETER Concurrency
    Number of simultaneous connections. Default: 150

.PARAMETER TimeoutSeconds
    Timeout per stream check in seconds. Default: 5

.EXAMPLE
    .\run_checker.ps1
    .\run_checker.ps1 -InputFile "index.m3u" -OutputFile "index1.m3u"
    .\run_checker.ps1 -Concurrency 200 -TimeoutSeconds 4
#>
param(
    [string]$InputFile = "index.m3u",
    [string]$OutputFile = "index1.m3u",
    [int]$Concurrency = 150,
    [int]$TimeoutSeconds = 5
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csPath = Join-Path $scriptDir "M3UChecker.cs"

if (-not (Test-Path $csPath)) {
    Write-Error "Could not find M3UChecker.cs at $csPath"
    exit 1
}

# Resolve input and output relative to current working directory
$resolvedInput = if ([System.IO.Path]::IsPathRooted($InputFile)) { $InputFile } else { Join-Path (Get-Location) $InputFile }
$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputFile)) { $OutputFile } else { Join-Path (Get-Location) $OutputFile }

if (-not (Test-Path $resolvedInput)) {
    Write-Error "Input file does not exist: $resolvedInput"
    exit 1
}

$code = [System.IO.File]::ReadAllText($csPath)
Add-Type -TypeDefinition $code -ReferencedAssemblies "System.Net.Http" -ErrorAction Stop

[M3UChecker]::Main(@(
    $resolvedInput,
    $resolvedOutput,
    $Concurrency.ToString(),
    $TimeoutSeconds.ToString()
))
