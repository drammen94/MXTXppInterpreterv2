<#
.SYNOPSIS
    Runs the native-vs-interpreted conformance suite on the AOS.

.DESCRIPTION
    Calls MXTXppInterpreterService/runConformance. Every case in MXTXppConformanceCases
    runs once as compiled X++ and once through the interpreter from the same source;
    each case whose emitted values differ is reported as FAIL with both results.
    All database work is rolled back. Exits with code 1 when any case fails.
#>
[CmdletBinding()]
param(
    [string] $EnvironmentUrl,
    [string] $AccessToken = $env:D365_ACCESS_TOKEN
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MXTXppClient.psm1') -Force

[void](Connect-MXTXpp -EnvironmentUrl $EnvironmentUrl -AccessToken $AccessToken -SkipWarmup)

try {
    $result = Invoke-MXTXppOperation -Operation 'runConformance'
    $report = [string]$result.Body

    foreach ($line in $report -split "`n") {
        $color = if ($line.StartsWith('FAIL')) { 'Red' } elseif ($line.StartsWith('PASS')) { 'Green' } else { 'Gray' }
        Write-Host $line -ForegroundColor $color
    }

    if ($report -match '(?m)^FAIL ') {
        exit 1
    }
}
finally {
    Disconnect-MXTXpp
}
