<#
.SYNOPSIS
    Interactive X++ prompt against the resident AOS interpreter, or rerun a file on every save.

.DESCRIPTION
    Opens one warm connection and token for the whole session, then either:
      - reads X++ at an interactive prompt (finish a snippet with an empty line), or
      - with -Watch, runs the file immediately and again each time it is saved.
    Every run is rollback-only. Nothing is compiled, deployed or synchronized.

.EXAMPLE
    .\Scripts\Start-MXTXppSession.ps1

.EXAMPLE
    .\Scripts\Start-MXTXppSession.ps1 -Watch .\scratch.xpp
#>
[CmdletBinding()]
param(
    [string] $EnvironmentUrl,
    [string] $AccessToken = $env:D365_ACCESS_TOKEN,
    [string] $Watch
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MXTXppClient.psm1') -Force

$connected = Connect-MXTXpp -EnvironmentUrl $EnvironmentUrl -AccessToken $AccessToken
Write-Host "Connected to $connected (warm). Every run is rolled back." -ForegroundColor DarkGray

function Invoke-Snippet([string] $code) {
    try {
        Write-MXTXppResult -Result (Invoke-MXTXpp -SourceCode $code)
    }
    catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
}

try {
    if ($Watch) {
        $path = (Resolve-Path -LiteralPath $Watch).Path
        $directory = Split-Path -Parent $path
        $fileName = Split-Path -Leaf $path

        Write-Host "Watching $path - save the file to run it, Ctrl+C to stop." -ForegroundColor DarkGray
        Invoke-Snippet (Get-Content -Raw -LiteralPath $path)

        $watcher = New-Object System.IO.FileSystemWatcher($directory, $fileName)
        $watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite -bor [System.IO.NotifyFilters]::Size
        $lastRun = [datetime]::MinValue

        while ($true) {
            $change = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::Changed, 1000)
            # Editors often write a file more than once per save
            if (-not $change.TimedOut -and ([datetime]::UtcNow - $lastRun).TotalMilliseconds -gt 250) {
                Start-Sleep -Milliseconds 50
                $lastRun = [datetime]::UtcNow
                Write-Host ("--- {0:HH:mm:ss}" -f (Get-Date)) -ForegroundColor DarkGray
                Invoke-Snippet (Get-Content -Raw -LiteralPath $path)
            }
        }
    }
    else {
        Write-Host 'Type X++ and finish with an empty line. :q quits.' -ForegroundColor DarkGray
        $buffer = New-Object System.Text.StringBuilder

        while ($true) {
            $prompt = if ($buffer.Length -eq 0) { 'x++> ' } else { '...  ' }
            Write-Host $prompt -NoNewline -ForegroundColor Cyan
            $line = [Console]::ReadLine()

            if ($null -eq $line -or ($buffer.Length -eq 0 -and $line.Trim() -eq ':q')) {
                break
            }

            if ($line.Trim().Length -eq 0) {
                if ($buffer.Length -gt 0) {
                    Invoke-Snippet $buffer.ToString()
                    [void]$buffer.Clear()
                }
                continue
            }

            [void]$buffer.AppendLine($line)
        }
    }
}
finally {
    Disconnect-MXTXpp
}
