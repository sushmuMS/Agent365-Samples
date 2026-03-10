# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Setup script for local Teams dev testing of the Agent Framework sample.
# Usage: .\setup-teams-dev.ps1 -BotId <id> -ClientSecret <secret> -TunnelUrl <url>
#
# Example:
#   .\setup-teams-dev.ps1 `
#     -BotId "0a5512fa-7e4c-4e09-aef1-b23fd8ea7e9e" `
#     -ClientSecret "Gde8Q~..." `
#     -TunnelUrl "https://sc1xm1g7-3978.usw2.devtunnels.ms"

param(
    [Parameter(Mandatory=$true)]
    [string]$BotId,

    [Parameter(Mandatory=$true)]
    [string]$ClientSecret,

    [Parameter(Mandatory=$true)]
    [string]$TunnelUrl,

    [string]$TenantId = "6e8b84fa-ae41-4a00-9ad1-934b73e5d73c",
    [string]$AzureOpenAIEndpoint = "https://mrunalhirve-2017-resource.openai.azure.com/",
    [string]$AzureOpenAIKey = "",
    [string]$AzureOpenAIDeployment = "gpt-4o",
    [string]$OpenWeatherKey = "",
    [switch]$SkipAgentRestart
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SampleDir = Join-Path $ScriptDir "..\dotnet\agent-framework\sample-agent"
$AppPackageDir = Join-Path $SampleDir "appPackage"

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Agent Framework - Teams Dev Setup Script" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------
# Step 1: Validate prerequisites
# ---------------------------------------------------------------
Write-Host "[1/5] Validating prerequisites..." -ForegroundColor Yellow

if (-not (Test-Path $SampleDir)) {
    Write-Error "Sample directory not found: $SampleDir"
    exit 1
}

try { dotnet --version | Out-Null } catch { Write-Error "dotnet SDK not found. Install from https://dotnet.microsoft.com/download"; exit 1 }

$tunnelDomain = ($TunnelUrl -replace "https://", "") -replace "/$", ""
Write-Host "  Bot ID     : $BotId"
Write-Host "  Tunnel URL : $TunnelUrl"
Write-Host "  Tenant ID  : $TenantId"
Write-Host "  Bot domain : $tunnelDomain"
Write-Host ""

# ---------------------------------------------------------------
# Step 2: Update appsettings.json
# ---------------------------------------------------------------
Write-Host "[2/5] Updating appsettings.json..." -ForegroundColor Yellow

$appSettingsPath = Join-Path $SampleDir "appsettings.json"
$content = Get-Content $appSettingsPath -Raw

# Update TokenValidation audience
$content = $content -replace '"Audiences":\s*\[\s*"[^"]*"', "`"Audiences`": [`n      `"$BotId`""

# Update ServiceConnection ClientId
$content = $content -replace '"ClientId":\s*"[^"]*",\s*// this is the Client ID used for the Azure Bot', "`"ClientId`": `"$BotId`", // this is the Client ID used for the Azure Bot"

# Update or insert ClientSecret
if ($content -match '"ClientSecret":\s*"[^"]*"') {
    $content = $content -replace '"ClientSecret":\s*"[^"]*"', "`"ClientSecret`": `"$ClientSecret`""
} else {
    $content = $content -replace '("ClientId":\s*"[^"]*",\s*// this is the Client ID used for the Azure Bot\s*\n)', "`$1        `"ClientSecret`": `"$ClientSecret`",`n"
}

# Update AuthType to ClientSecret
$content = $content -replace '"AuthType":\s*"UserManagedIdentity"', '"AuthType": "ClientSecret"'

# Update Scopes
$content = $content -replace '"5a807f24-c9de-44ee-a3a7-329e88a00ffc/\.default"', '"https://api.botframework.com/.default"'

# Update Azure OpenAI if provided
if ($AzureOpenAIKey -ne "") {
    $content = $content -replace '"ApiKey":\s*"[^"]*"', "`"ApiKey`": `"$AzureOpenAIKey`""
}

Set-Content $appSettingsPath $content -Encoding UTF8
Write-Host "  appsettings.json updated" -ForegroundColor Green

# ---------------------------------------------------------------
# Step 3: Update appsettings.Playground.json
# ---------------------------------------------------------------
Write-Host "[3/5] Updating appsettings.Playground.json..." -ForegroundColor Yellow

$playgroundPath = Join-Path $SampleDir "appsettings.Playground.json"
$pgContent = Get-Content $playgroundPath -Raw

$pgContent = $pgContent -replace '"Audiences":\s*\[\s*\n?\s*"[^"]*"', "`"Audiences`": [`n      `"$BotId`""
$pgContent = $pgContent -replace '"ClientId":\s*"[^"]*",\s*// this is the Client ID used for the Azure Bot', "`"ClientId`": `"$BotId`", // this is the Client ID used for the Azure Bot"
$pgContent = $pgContent -replace '"ClientSecret":\s*"[^"]*"', "`"ClientSecret`": `"$ClientSecret`""

if ($AzureOpenAIKey -ne "") {
    $pgContent = $pgContent -replace '"ApiKey":\s*"[^"]*"', "`"ApiKey`": `"$AzureOpenAIKey`""
}

Set-Content $playgroundPath $pgContent -Encoding UTF8
Write-Host "  appsettings.Playground.json updated" -ForegroundColor Green

# ---------------------------------------------------------------
# Step 4: Build Teams app manifest zip
# ---------------------------------------------------------------
Write-Host "[4/5] Building Teams app manifest zip..." -ForegroundColor Yellow

$manifestTemplate = Join-Path $AppPackageDir "manifest.json"
$zipPath = Join-Path $AppPackageDir "AgentSample-teamsapp.zip"
$tempDir = Join-Path $env:TEMP "teamsapp-$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir | Out-Null

$manifest = Get-Content $manifestTemplate -Raw | ConvertFrom-Json
$manifest.id = [guid]::NewGuid().ToString()
$manifest.bots[0].botId = $BotId
$manifest.copilotAgents.customEngineAgents[0].id = $BotId

# Update validDomains
$manifest.validDomains = @($tunnelDomain)

$manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $tempDir "manifest.json") -Encoding UTF8
Copy-Item (Join-Path $AppPackageDir "color.png") $tempDir
Copy-Item (Join-Path $AppPackageDir "outline.png") $tempDir

Compress-Archive -Path "$tempDir\*" -DestinationPath $zipPath -Force
Remove-Item $tempDir -Recurse -Force

Write-Host "  Manifest zip created: $zipPath" -ForegroundColor Green

# ---------------------------------------------------------------
# Step 5: Restart agent
# ---------------------------------------------------------------
if (-not $SkipAgentRestart) {
    Write-Host "[5/5] Restarting agent..." -ForegroundColor Yellow

    $existing = Get-Process -Name "AgentFrameworkSampleAgent" -ErrorAction SilentlyContinue
    if ($existing) {
        $existing | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Host "  Stopped existing agent process" -ForegroundColor Gray
    }

    $agentProc = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --launch-profile AgentFrameworkSampleAgent" `
        -WorkingDirectory $SampleDir `
        -PassThru -WindowStyle Hidden

    Start-Sleep -Seconds 10

    try {
        $health = Invoke-RestMethod -Uri "http://localhost:3978/api/health" -TimeoutSec 5
        Write-Host "  Agent running: $($health.status) (PID $($agentProc.Id))" -ForegroundColor Green
    } catch {
        Write-Warning "  Agent health check failed - it may still be starting up"
    }
} else {
    Write-Host "[5/5] Skipping agent restart (-SkipAgentRestart)" -ForegroundColor Gray
}

# ---------------------------------------------------------------
# Summary
# ---------------------------------------------------------------
Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "  Setup Complete!" -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor White
Write-Host ""
Write-Host "  1. Update Azure Bot messaging endpoint:" -ForegroundColor White
Write-Host "     $TunnelUrl/api/messages" -ForegroundColor Yellow
Write-Host "     (Teams Dev Portal -> Bot Management -> $BotId -> Configure)" -ForegroundColor Gray
Write-Host ""
Write-Host "  2. Upload Teams app:" -ForegroundColor White
Write-Host "     $zipPath" -ForegroundColor Yellow
Write-Host "     (Teams -> Apps -> Manage your apps -> Upload a custom app)" -ForegroundColor Gray
Write-Host ""
Write-Host "  3. Test in Teams by sending a message to the bot" -ForegroundColor White
Write-Host ""
