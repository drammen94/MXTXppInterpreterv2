<#
.SYNOPSIS
    X++ notebook against the resident AOS interpreter: an interactive prompt, or a file of cells
    that reruns on every save.

.DESCRIPTION
    Opens one warm connection, token and notebook session. Every snippet or cell runs in that
    notebook, so variables, script classes and using imports declared earlier stay available,
    as in a Jupyter notebook. Nothing is compiled, deployed or synchronized.

    Interactive: type X++ and finish a cell with an empty line.
        :vars   show the notebook's variables
        :reset  start a fresh notebook
        :q      quit

    -Watch: cells in the file are separated by lines starting with // %%. On each save the
    first changed cell and every cell after it run again; unchanged cells above it keep their
    results, so editing the last cell reruns only that cell.

    Database work is rolled back after every cell unless -Commit is given.

.EXAMPLE
    .\Scripts\Start-MXTXppSession.ps1

.EXAMPLE
    .\Scripts\Start-MXTXppSession.ps1 -Watch .\scratch.xpp
#>
[CmdletBinding()]
param(
    [string] $EnvironmentUrl,
    [string] $AccessToken = $env:D365_ACCESS_TOKEN,
    [string] $Watch,
    [switch] $Commit
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MXTXppClient.psm1') -Force

$connected = Connect-MXTXpp -EnvironmentUrl $EnvironmentUrl -AccessToken $AccessToken
$mode = if ($Commit) { 'Cells commit their database work.' } else { 'Every cell is rolled back.' }
Write-Host "Connected to $connected (warm notebook). $mode" -ForegroundColor DarkGray

function Invoke-Cell([string] $code) {
    try {
        $result = Invoke-MXTXppCell -SourceCode $code -Commit:$Commit
        Write-MXTXppResult -Result $result
        return $result.Success
    }
    catch {
        Write-Host $_.Exception.Message -ForegroundColor Red
        return $false
    }
}

function Split-Cells([string] $text) {
    $cells = New-Object System.Collections.Generic.List[string]
    $current = New-Object System.Text.StringBuilder

    foreach ($line in ($text -split "\r?\n")) {
        if ($line -match '^\s*//\s*%%') {
            if ($current.ToString().Trim()) { $cells.Add($current.ToString()) }
            [void]$current.Clear()
        }
        else {
            [void]$current.AppendLine($line)
        }
    }

    if ($current.ToString().Trim()) { $cells.Add($current.ToString()) }
    return , $cells.ToArray()
}

try {
    if ($Watch) {
        $path = (Resolve-Path -LiteralPath $Watch).Path
        $directory = Split-Path -Parent $path
        $fileName = Split-Path -Leaf $path
        $ran = @()

        $runChanged = {
            $cells = Split-Cells (Get-Content -Raw -LiteralPath $path)
            $first = 0

            while ($first -lt $cells.Count -and $first -lt $ran.Count -and $cells[$first] -ceq $ran[$first]) {
                $first++
            }

            if ($first -ge $cells.Count) {
                Write-Host 'No changed cells.' -ForegroundColor DarkGray
                return
            }

            $script:ranNext = if ($first -gt 0) { @($cells[0..($first - 1)]) } else { @() }

            for ($i = $first; $i -lt $cells.Count; $i++) {
                Write-Host ("--- cell {0}/{1}" -f ($i + 1), $cells.Count) -ForegroundColor DarkCyan
                if (-not (Invoke-Cell $cells[$i])) {
                    # Cells after a failure would run against a half-finished state
                    break
                }
                $script:ranNext += $cells[$i]
            }
        }

        Write-Host "Watching $path - cells split on // %%, save to run changed cells, Ctrl+C to stop." -ForegroundColor DarkGray
        & $runChanged
        $ran = $script:ranNext

        $watcher = New-Object System.IO.FileSystemWatcher($directory, $fileName)
        $watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite -bor [System.IO.NotifyFilters]::Size
        $lastRun = [datetime]::MinValue

        while ($true) {
            $change = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::Changed, 1000)
            # Editors often write a file more than once per save
            if (-not $change.TimedOut -and ([datetime]::UtcNow - $lastRun).TotalMilliseconds -gt 250) {
                Start-Sleep -Milliseconds 50
                $lastRun = [datetime]::UtcNow
                Write-Host ("=== {0:HH:mm:ss}" -f (Get-Date)) -ForegroundColor DarkGray
                & $runChanged
                $ran = $script:ranNext
            }
        }
    }
    else {
        Write-Host 'Type X++ and finish a cell with an empty line. :vars, :reset, :q' -ForegroundColor DarkGray
        $buffer = New-Object System.Text.StringBuilder
        $lastVariables = ''

        while ($true) {
            $prompt = if ($buffer.Length -eq 0) { 'x++> ' } else { '...  ' }
            Write-Host $prompt -NoNewline -ForegroundColor Cyan
            $line = [Console]::ReadLine()

            if ($null -eq $line) {
                break
            }

            if ($buffer.Length -eq 0) {
                $command = $line.Trim()

                if ($command -eq ':q') {
                    break
                }
                elseif ($command -eq ':reset') {
                    Reset-MXTXppNotebook
                    $lastVariables = ''
                    Write-Host 'Fresh notebook.' -ForegroundColor DarkGray
                    continue
                }
                elseif ($command -eq ':vars') {
                    Write-Host ($lastVariables.TrimEnd()) -ForegroundColor Gray
                    continue
                }
            }

            if ($line.Trim().Length -eq 0) {
                if ($buffer.Length -gt 0) {
                    try {
                        $result = Invoke-MXTXppCell -SourceCode $buffer.ToString() -Commit:$Commit
                        Write-MXTXppResult -Result $result
                        if ($result.Variables) { $lastVariables = [string]$result.Variables }
                    }
                    catch {
                        Write-Host $_.Exception.Message -ForegroundColor Red
                    }
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
