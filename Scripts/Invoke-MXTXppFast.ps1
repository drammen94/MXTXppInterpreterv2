[CmdletBinding()]
param(
    [string] $EnvironmentUrl,

    [Parameter(Mandatory, ParameterSetName = 'Source')]
    [string] $SourceCode,

    [Parameter(Mandatory, ParameterSetName = 'File')]
    [string] $Path,

    [string] $AccessToken = $env:D365_ACCESS_TOKEN,

    [ValidateRange(1, 10000)]
    [int] $Iterations = 1,

    [ValidateRange(0, 1000)]
    [int] $Warmup = 0
)

$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -eq 'File') {
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $SourceCode = Get-Content -Raw -LiteralPath $resolvedPath
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

$endpoint = $EnvironmentUrl + '/api/services/MXTXppInterpreterServiceGroup/MXTXppInterpreterService/execute'
$headers = @{
    Authorization = 'Bearer ' + $AccessToken
    Accept = 'application/json'
}

function Invoke-FastExecution {
    param([string] $Code)

    $requestId = [Guid]::NewGuid().ToString('D')
    $body = @{
        _request = @{
            SourceCode = $Code
            RequestId = $requestId
        }
    } | ConvertTo-Json -Depth 5 -Compress

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType 'application/json' -Body $body
    $stopwatch.Stop()

    [pscustomobject]@{
        RequestId = $requestId
        HttpMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
        Response = $response
    }
}

for ($index = 0; $index -lt $Warmup; $index++) {
    [void](Invoke-FastExecution -Code $SourceCode)
}

$samples = for ($index = 0; $index -lt $Iterations; $index++) {
    Invoke-FastExecution -Code $SourceCode
}

$failed = @($samples | Where-Object { -not $_.Response.Success })
if ($failed.Count -gt 0) {
    $failed | ForEach-Object {
        [pscustomobject]@{
            RequestId = $_.RequestId
            Error = $_.Response.Error
            Output = $_.Response.Output
        }
    } | Format-List | Out-String | Write-Error
    exit 1
}

$ordered = @($samples.HttpMilliseconds | Sort-Object)
$mean = ($samples.HttpMilliseconds | Measure-Object -Average).Average
$p50 = $ordered[[Math]::Max(0, [Math]::Ceiling(0.50 * $ordered.Count) - 1)]
$p95 = $ordered[[Math]::Max(0, [Math]::Ceiling(0.95 * $ordered.Count) - 1)]
$last = $samples[-1].Response

[pscustomobject]@{
    Endpoint = $endpoint
    Iterations = $Iterations
    HttpMeanMilliseconds = [Math]::Round($mean, 3)
    HttpP50Milliseconds = [Math]::Round($p50, 3)
    HttpP95Milliseconds = [Math]::Round($p95, 3)
    ServerTotalMilliseconds = [Math]::Round(([double]$last.TotalMicroseconds / 1000), 3)
    ServerParseMilliseconds = [Math]::Round(([double]$last.ParseMicroseconds / 1000), 3)
    ServerCompileMilliseconds = [Math]::Round(([double]$last.CompileMicroseconds / 1000), 3)
    ServerExecuteMilliseconds = [Math]::Round(([double]$last.ExecuteMicroseconds / 1000), 3)
    InstructionCount = $last.InstructionCount
    RolledBack = $last.RolledBack
    Output = $last.Output
} | Format-List
