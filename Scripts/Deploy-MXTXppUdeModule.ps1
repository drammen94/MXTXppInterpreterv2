[CmdletBinding()]
param(
    [string] $ModuleName = 'MXppTools',
    [string] $ModuleRoot,
    [string] $CompiledBin,
    [string] $ConfigurationPath,
    [string] $ToolkitRoot = (Join-Path $env:USERPROFILE '.codex\mcp-servers\d365fo-toolkit\mcp-servers\d365-xpp'),
    [int] $TimeoutSec = 900
)

$ErrorActionPreference = 'Stop'
$hostExe = Join-Path $ToolkitRoot 'headless\bin\D365XppUdeHost.exe'
$proxyScript = Join-Path $ToolkitRoot 'headless\ipv4-connect-proxy.js'

function Get-ActiveXppConfigurationPath {
    if ($ConfigurationPath) { return $ConfigurationPath }
    if ($env:D365_XPP_CONFIG) { return $env:D365_XPP_CONFIG }

    $key = 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations'
    $value = (Get-ItemProperty -LiteralPath $key).CurrentMetadataConfig
    if (-not $value) { throw 'No active Finance and Operations X++ configuration is registered.' }
    return [string]$value
}

function Get-VisualStudioInstallPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    foreach ($path in @(& $vswhere -all -products * -version '[17.0,18.0)' -property installationPath)) {
        $extensions = Join-Path $path 'Common7\IDE\Extensions'
        if (Get-ChildItem -LiteralPath $extensions -Recurse -Filter 'Microsoft.PowerPlatform.VSShared.Util.dll' -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
            return [string]$path
        }
    }
    throw 'A Visual Studio installation containing Power Platform Tools was not found.'
}

function Get-ExtensionRoots([string] $VisualStudioInstallPath) {
    $root = Join-Path $VisualStudioInstallPath 'Common7\IDE\Extensions'
    $powerPlatform = Get-ChildItem -LiteralPath $root -Recurse -Filter 'Microsoft.PowerPlatform.VSShared.Util.dll' -File |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName 'Microsoft.Xrm.Tooling.Connector.dll') } |
        Select-Object -First 1
    $financeOperations = Get-ChildItem -LiteralPath $root -Recurse -Filter 'Microsoft.Dynamics.TestTools.17.0.TestAdapter.dll' -File |
        Select-Object -First 1
    if (-not $powerPlatform -or -not $financeOperations) { throw 'Required Power Platform or Finance and Operations extension assemblies were not found.' }
    return [pscustomobject]@{
        PowerPlatform = $powerPlatform.DirectoryName
        FinanceOperations = $financeOperations.DirectoryName
    }
}

function Start-Ipv4Proxy {
    $node = (Get-Command node -ErrorAction Stop).Source
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $port = Get-Random -Minimum 21000 -Maximum 49000
        $process = Start-Process -FilePath $node -ArgumentList @("`"$proxyScript`"", [string]$port) -WindowStyle Hidden -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            if ($process.HasExited) { break }
            $client = New-Object Net.Sockets.TcpClient
            try {
                $connected = $client.ConnectAsync('127.0.0.1', $port)
                if ($connected.Wait(200) -and $client.Connected) {
                    return [pscustomobject]@{ Process = $process; Port = $port }
                }
            }
            catch { }
            finally { $client.Dispose() }
        } while ([DateTime]::UtcNow -lt $deadline)
        if (-not $process.HasExited) { try { $process.Kill() } catch { } }
    }
    throw 'Could not start the IPv4 CONNECT proxy.'
}

$configPath = Get-ActiveXppConfigurationPath
$config = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
if ([int]$config.RuntimeHostType -ne 4) { throw 'The active X++ configuration is not a Unified cloud runtime.' }

$referenceRoot = @($config.ReferencePackagesPaths | Where-Object { Test-Path -LiteralPath $_ })[0]
if (-not $referenceRoot) { throw 'The active X++ configuration has no accessible reference package root.' }
if (-not $ModuleRoot) { $ModuleRoot = Join-Path $referenceRoot $ModuleName }
$ModuleRoot = (Resolve-Path -LiteralPath $ModuleRoot).Path
$moduleBin = Join-Path $ModuleRoot 'bin'

if ($CompiledBin) {
    $CompiledBin = (Resolve-Path -LiteralPath $CompiledBin).Path
    Get-ChildItem -LiteralPath $CompiledBin -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $moduleBin $_.Name) -Force
    }
}

$moduleDll = Join-Path $moduleBin "Dynamics.AX.$ModuleName.dll"
if (-not (Test-Path -LiteralPath $moduleDll)) { throw "Compiled module not found: $moduleDll" }
if (-not (Test-Path -LiteralPath $hostExe)) { throw "Unified deployment host not found: $hostExe" }

$visualStudio = Get-VisualStudioInstallPath
$extensions = Get-ExtensionRoots $visualStudio
$crmConfigPath = Join-Path $env:APPDATA 'Microsoft\CRMDeveloperToolKit\Default_CRMDeveloperToolKit.exe.config'
if (-not (Test-Path -LiteralPath $crmConfigPath)) { throw "Power Platform connection profile not found: $crmConfigPath" }

[xml]$crmConfig = Get-Content -LiteralPath $crmConfigPath
$crmSettings = @{}
foreach ($node in $crmConfig.configuration.appSettings.add) { $crmSettings[$node.key] = $node.value }
$directUri = [uri]$crmSettings['DirectConnectionUri']
$tenantMatch = [regex]::Match([string]$crmSettings['Authority'], '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $tenantMatch.Success) { throw 'The tenant ID was not found in the Power Platform connection profile.' }

$applicationVersion = ''
$applicationMatch = [regex]::Match([string]$config.Description, 'application version\s+(?<version>[0-9]+(?:\.[0-9]+)+)', 'IgnoreCase')
if ($applicationMatch.Success) { $applicationVersion = $applicationMatch.Groups['version'].Value }
if (-not $applicationVersion) { $applicationVersion = Split-Path -Leaf (Split-Path -Parent $referenceRoot) }
$xppc = Join-Path $referenceRoot 'bin\xppc.exe'
$platformVersion = (Get-Item -LiteralPath $xppc).VersionInfo.FileVersion

$proxy = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
try {
    $proxy = Start-Ipv4Proxy
    $env:D365_UDE_HTTP_PROXY = "http://127.0.0.1:$($proxy.Port)"
    $env:D365_UDE_ORG_URL = '{0}://{1}' -f $directUri.Scheme, $directUri.Host
    $env:D365_UDE_TENANT_ID = $tenantMatch.Value
    $env:D365_UDE_USER_NAME = [string]$crmSettings['UserId']
    $profileTokenCache = Join-Path $env:APPDATA 'Microsoft\CRMDeveloperToolKit\Default_CRMDeveloperToolKit.tokens.dat'
    $env:D365_UDE_CONNECTION_STRING = 'AuthType=OAuth;Username={0};Url={1};AppId=2ad88395-b77d-4561-9441-d0e40824f9bc;RedirectUri=app://5d3e90d6-aa8e-48a8-8f2c-58b45cc67315;TokenCacheStorePath={2};LoginPrompt=Never;SkipDiscovery=true;RequireNewInstance=true' -f
        [string]$crmSettings['UserId'], $env:D365_UDE_ORG_URL, $profileTokenCache
    $env:D365_UDE_ALLOW_DEVICE_CODE = 'false'
    $env:D365_UDE_MSAL_CACHE_PATH = Join-Path $env:LOCALAPPDATA 'Microsoft\Dynamics365\CodexHeadless\Dataverse.msalcache'
    $env:D365_UDE_PROBE_ROOTS = @(
        $extensions.PowerPlatform,
        $extensions.FinanceOperations,
        (Join-Path $referenceRoot 'bin\ModelUtilDlls'),
        (Join-Path $referenceRoot 'bin')
    ) -join '|'

    $output = & $hostExe deploy $ModuleRoot $ModuleName $platformVersion $applicationVersion 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
    $jsonLine = @($output | Where-Object { ([string]$_).TrimStart().StartsWith('{') }) | Select-Object -Last 1
    if (-not $jsonLine) { throw 'The Unified deployment host returned no JSON result.' }
    $result = ([string]$jsonLine | ConvertFrom-Json)
    $stopwatch.Stop()
    [pscustomobject]@{
        Module = $ModuleName
        EnvironmentUrl = $env:D365_UDE_ORG_URL
        Deployed = [bool]$result.deployed
        CorrelationId = $result.correlationId
        ElapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    }
}
finally {
    if ($proxy -and $proxy.Process -and -not $proxy.Process.HasExited) {
        try { $proxy.Process.Kill() } catch { }
    }
}
