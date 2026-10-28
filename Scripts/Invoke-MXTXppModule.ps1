[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Deploy', 'Activate', 'Rollback', 'Execute', 'Get', 'List')]
    [string] $Action,

    [string] $ModuleName,
    [string] $SourceCode,
    [string] $Path,
    [string[]] $Dependency = @(),
    [ValidateRange(0, 2147483647)]
    [int] $Version = 0,
    [bool] $Activate = $true,
    [string] $EnvironmentUrl,
    [string] $AccessToken = $env:D365_ACCESS_TOKEN,
    [ValidateRange(1, 10000)]
    [int] $Iterations = 1,
    [ValidateRange(0, 1000)]
    [int] $Warmup = 0
)

$ErrorActionPreference = 'Stop'

if ($Action -ne 'List' -and -not $ModuleName) {
    throw "-ModuleName is required for $Action."
}
if ($Path) {
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $SourceCode = Get-Content -Raw -LiteralPath $resolvedPath
}
if ($Action -eq 'Deploy' -and -not $SourceCode) {
    throw 'Deploy requires -SourceCode or -Path.'
}

if (-not $EnvironmentUrl) {
    $crmConfigPath = Join-Path $env:APPDATA 'Microsoft\CRMDeveloperToolKit\Default_CRMDeveloperToolKit.exe.config'
    if (-not (Test-Path -LiteralPath $crmConfigPath)) {
        throw 'Supply -EnvironmentUrl, or configure the Finance and Operations developer connection profile.'
    }
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Azure CLI is required to resolve the Finance and Operations URL automatically.'
    }

    [xml]$crmConfig = Get-Content -LiteralPath $crmConfigPath
    $crmSettings = @{}
    foreach ($node in $crmConfig.configuration.appSettings.add) { $crmSettings[$node.key] = $node.value }
    $dataverseUrl = ([uri]$crmSettings['DirectConnectionUri']).GetLeftPart([UriPartial]::Authority)
    $dataverseToken = (az account get-access-token --resource $dataverseUrl --output json) | ConvertFrom-Json
    if (-not $dataverseToken.accessToken) { throw 'Azure CLI did not return a Dataverse access token.' }
    $integration = Invoke-RestMethod -Method Get `
        -Uri ($dataverseUrl + '/api/data/v9.1/RetrieveFinanceAndOperationsIntegrationDetails') `
        -Headers @{ Authorization = 'Bearer ' + $dataverseToken.accessToken; Accept = 'application/json' }
    $EnvironmentUrl = [string]$integration.Url
}

$EnvironmentUrl = $EnvironmentUrl.TrimEnd('/')
if (-not $AccessToken) {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Supply -AccessToken, set D365_ACCESS_TOKEN, or install and sign in to Azure CLI.'
    }
    $tokenResult = (az account get-access-token --resource $EnvironmentUrl --output json) | ConvertFrom-Json
    $AccessToken = [string]$tokenResult.accessToken
}
if (-not $AccessToken) { throw 'No Finance and Operations access token is available.' }

$operation = @{
    Deploy = 'deployModule'
    Activate = 'activateModule'
    Rollback = 'rollbackModule'
    Execute = 'executeModule'
    Get = 'getModule'
    List = 'listModules'
}[$Action]
$endpoint = "$EnvironmentUrl/api/services/MXTXppInterpreterServiceGroup/MXTXppInterpreterService/$operation"
$headers = @{ Authorization = 'Bearer ' + $AccessToken; Accept = 'application/json' }

function Invoke-ModuleOperation {
    $requestId = [Guid]::NewGuid().ToString('D')
    if ($Action -eq 'List') {
        $body = @{} | ConvertTo-Json -Compress
    }
    else {
        $body = @{
            _request = @{
                ModuleName = $ModuleName
                SourceCode = if ($SourceCode) { $SourceCode } else { '' }
                Dependencies = @($Dependency)
                Version = $Version
                Activate = $Activate
                RequestId = $requestId
            }
        } | ConvertTo-Json -Depth 8 -Compress
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType 'application/json' -Body $body
    $stopwatch.Stop()
    [pscustomobject]@{
        RequestId = $requestId
        HttpMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
        Response = $response
    }
}

if ($Action -ne 'Execute') {
    $result = Invoke-ModuleOperation
    if (-not $result.Response.Success) {
        $result.Response | Format-List | Out-String | Write-Error
        exit 1
    }
    [pscustomobject]@{
        Endpoint = $endpoint
        HttpMilliseconds = [Math]::Round($result.HttpMilliseconds, 3)
        Result = $result.Response
    }
    return
}

for ($warmupIndex = 0; $warmupIndex -lt $Warmup; $warmupIndex++) {
    [void](Invoke-ModuleOperation)
}
$samples = for ($sampleIndex = 0; $sampleIndex -lt $Iterations; $sampleIndex++) {
    Invoke-ModuleOperation
}
$failed = @($samples | Where-Object { -not $_.Response.Success })
if ($failed.Count) {
    $failed | ForEach-Object { $_.Response } | Format-List | Out-String | Write-Error
    exit 1
}

$ordered = @($samples.HttpMilliseconds | Sort-Object)
$last = $samples[-1].Response
[pscustomobject]@{
    Endpoint = $endpoint
    Module = $ModuleName
    Version = $last.ModuleVersion
    Iterations = $Iterations
    HttpMeanMilliseconds = [Math]::Round((($samples.HttpMilliseconds | Measure-Object -Average).Average), 3)
    HttpP50Milliseconds = [Math]::Round($ordered[[Math]::Max(0, [Math]::Ceiling(0.50 * $ordered.Count) - 1)], 3)
    HttpP95Milliseconds = [Math]::Round($ordered[[Math]::Max(0, [Math]::Ceiling(0.95 * $ordered.Count) - 1)], 3)
    ServerTotalMilliseconds = [Math]::Round(([double]$last.TotalMicroseconds / 1000), 3)
    ServerParseMilliseconds = [Math]::Round(([double]$last.ParseMicroseconds / 1000), 3)
    ServerCompileMilliseconds = [Math]::Round(([double]$last.CompileMicroseconds / 1000), 3)
    ServerExecuteMilliseconds = [Math]::Round(([double]$last.ExecuteMicroseconds / 1000), 3)
    CacheHit = $last.CacheHit
    InstructionCount = $last.InstructionCount
    RolledBack = $last.RolledBack
    Output = $last.Output
}
