#Requires -Version 5.1
<#
.SYNOPSIS
    Warm client for the MXTXppInterpreterService custom service.

.DESCRIPTION
    Keeps one HTTP connection (keep-alive) and one access token for the whole session,
    so each run costs a single request instead of a TLS handshake, an Azure CLI token
    call and a connection setup. Used by Start-MXTXppSession.ps1 and
    Invoke-MXTXppConformance.ps1.
#>

Set-StrictMode -Version Latest

$script:Session = $null

function Resolve-MXTXppEnvironmentUrl {
    [CmdletBinding()]
    param([string] $EnvironmentUrl)

    if ($EnvironmentUrl) {
        return $EnvironmentUrl.TrimEnd('/')
    }

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

    return ([string]$integration.Url).TrimEnd('/')
}

function Get-MXTXppAccessToken {
    # Tokens are reused until five minutes before expiry; an explicit token is used as given
    $session = $script:Session

    if ($session.FixedToken) {
        return $session.FixedToken
    }

    if ($session.Token -and [DateTimeOffset]::UtcNow -lt $session.TokenExpiresUtc.AddMinutes(-5)) {
        return $session.Token
    }

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Supply -AccessToken, set D365_ACCESS_TOKEN, or install and sign in to Azure CLI.'
    }

    $tokenResult = (az account get-access-token --resource $session.EnvironmentUrl --output json) | ConvertFrom-Json
    if (-not $tokenResult.accessToken) { throw 'Azure CLI did not return a Finance and Operations access token.' }

    $session.Token = [string]$tokenResult.accessToken
    $session.TokenExpiresUtc = if ($tokenResult.PSObject.Properties['expires_on'] -and $tokenResult.expires_on) {
        [DateTimeOffset]::FromUnixTimeSeconds([int64]$tokenResult.expires_on)
    }
    else {
        [DateTimeOffset]::new([datetime]::Parse($tokenResult.expiresOn)).ToUniversalTime()
    }

    return $session.Token
}

function Connect-MXTXpp {
    <#
    .SYNOPSIS
        Opens the warm session: resolves the environment, fetches a token, and opens a
        keep-alive connection by sending one tiny warm-up script.
    #>
    [CmdletBinding()]
    param(
        [string] $EnvironmentUrl,
        [string] $AccessToken = $env:D365_ACCESS_TOKEN,
        [switch] $SkipWarmup
    )

    Disconnect-MXTXpp

    Add-Type -AssemblyName System.Net.Http

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $client.DefaultRequestHeaders.Accept.Add([System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new('application/json'))
    $client.DefaultRequestHeaders.ConnectionClose = $false

    $resolvedUrl = Resolve-MXTXppEnvironmentUrl -EnvironmentUrl $EnvironmentUrl

    $script:Session = [pscustomobject]@{
        EnvironmentUrl = $resolvedUrl
        ServiceRoot = $resolvedUrl + '/api/services/MXTXppInterpreterServiceGroup/MXTXppInterpreterService'
        Client = $client
        NotebookId = [Guid]::NewGuid().ToString('D')
        FixedToken = $AccessToken
        Token = $null
        TokenExpiresUtc = [DateTimeOffset]::MinValue
    }

    [void](Get-MXTXppAccessToken)

    if (-not $SkipWarmup) {
        # Opens the TCP/TLS connection and warms the AOS-side interpreter and metadata caches
        [void](Invoke-MXTXpp -SourceCode 'int warmup = 1;')
    }

    $script:Session.EnvironmentUrl
}

function Disconnect-MXTXpp {
    if ($script:Session -and $script:Session.Client) {
        # Frees the notebook's variables on the AOS; the session also expires there when idle
        try { Reset-MXTXppNotebook } catch { }
        $script:Session.Client.Dispose()
    }

    $script:Session = $null
}

function Invoke-MXTXppOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Operation,
        [string] $JsonBody = '{}'
    )

    if (-not $script:Session) {
        throw 'Run Connect-MXTXpp first.'
    }

    $uri = [Uri]::new($script:Session.ServiceRoot + '/' + $Operation)
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', (Get-MXTXppAccessToken))
    $request.Content = [System.Net.Http.StringContent]::new($JsonBody, [System.Text.Encoding]::UTF8, 'application/json')

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $response = $script:Session.Client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $stopwatch.Stop()

    if (-not $response.IsSuccessStatusCode) {
        throw "MXTXppInterpreterService/$Operation returned HTTP $([int]$response.StatusCode): $text"
    }

    [pscustomobject]@{
        HttpMilliseconds = $stopwatch.Elapsed.TotalMilliseconds
        Body = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Invoke-MXTXpp {
    <#
    .SYNOPSIS
        Runs X++ source through the rollback-only execute operation on the warm session.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory, ValueFromPipeline)] [string] $SourceCode)

    process {
        $requestId = [Guid]::NewGuid().ToString('D')
        $body = @{ _request = @{ SourceCode = $SourceCode; RequestId = $requestId } } | ConvertTo-Json -Depth 5 -Compress
        $result = Invoke-MXTXppOperation -Operation 'execute' -JsonBody $body
        $response = $result.Body

        [pscustomobject]@{
            Success = [bool]$response.Success
            Output = $response.Output
            Error = $response.Error
            HttpMilliseconds = [Math]::Round($result.HttpMilliseconds, 1)
            ServerMilliseconds = [Math]::Round(([double]$response.TotalMicroseconds / 1000), 2)
            ParseMilliseconds = [Math]::Round(([double]$response.ParseMicroseconds / 1000), 2)
            ExecuteMilliseconds = [Math]::Round(([double]$response.ExecuteMicroseconds / 1000), 2)
            RolledBack = [bool]$response.RolledBack
            RequestId = $requestId
        }
    }
}

function Invoke-MXTXppCell {
    <#
    .SYNOPSIS
        Runs one notebook cell. Cells of the same notebook share variables, script classes and
        using imports on the AOS; nothing is compiled to metadata.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)] [string] $SourceCode,
        [switch] $Commit
    )

    process {
        if (-not $script:Session) {
            throw 'Run Connect-MXTXpp first.'
        }

        $requestId = [Guid]::NewGuid().ToString('D')
        $request = @{ SessionId = $script:Session.NotebookId; SourceCode = $SourceCode; Commit = [bool]$Commit; RequestId = $requestId }
        $body = @{ _request = $request } | ConvertTo-Json -Depth 5 -Compress
        $result = Invoke-MXTXppOperation -Operation 'executeCell' -JsonBody $body
        $response = $result.Body

        [pscustomobject]@{
            Success = [bool]$response.Success
            Output = $response.Output
            Error = $response.Error
            Variables = $response.Variables
            CellNumber = [int]$response.CellNumber
            Cached = [bool]$response.CacheHit
            HttpMilliseconds = [Math]::Round($result.HttpMilliseconds, 1)
            ServerMilliseconds = [Math]::Round(([double]$response.TotalMicroseconds / 1000), 2)
            ParseMilliseconds = [Math]::Round(([double]$response.ParseMicroseconds / 1000), 2)
            ExecuteMilliseconds = [Math]::Round(([double]$response.ExecuteMicroseconds / 1000), 2)
            RolledBack = [bool]$response.RolledBack
            RequestId = $requestId
        }
    }
}

function Reset-MXTXppNotebook {
    <#
    .SYNOPSIS
        Drops the notebook's variables, classes and imports on the AOS and starts a fresh one.
    #>
    if (-not $script:Session) {
        return
    }

    $body = @{ _request = @{ SessionId = $script:Session.NotebookId } } | ConvertTo-Json -Depth 5 -Compress
    [void](Invoke-MXTXppOperation -Operation 'resetNotebook' -JsonBody $body)
    $script:Session.NotebookId = [Guid]::NewGuid().ToString('D')
}

function Write-MXTXppResult {
    param([Parameter(Mandatory)] $Result)

    if ($Result.Output) {
        Write-Host ($Result.Output.TrimEnd())
    }

    if (-not $Result.Success) {
        Write-Host ($Result.Error.TrimEnd()) -ForegroundColor Red
    }

    $color = if ($Result.Success) { 'DarkGray' } else { 'DarkRed' }
    $database = if ($Result.RolledBack -or -not $Result.Success) { 'rolled back' } else { 'committed' }
    $cell = if ($Result.PSObject.Properties['CellNumber'] -and $Result.CellNumber) { "cell $($Result.CellNumber), " } else { '' }
    Write-Host ("[{0}{1} ms round trip, {2} ms on server, {3}]" -f $cell, $Result.HttpMilliseconds, $Result.ServerMilliseconds, $database) -ForegroundColor $color
}

Export-ModuleMember -Function Connect-MXTXpp, Disconnect-MXTXpp, Invoke-MXTXpp, Invoke-MXTXppCell, Reset-MXTXppNotebook, Invoke-MXTXppOperation, Write-MXTXppResult
