#requires -Version 7.0
<#
.SYNOPSIS
    Full setup script for the Semantic Kernel sample agent.

.DESCRIPTION
    1. Fetches a fresh bearer token via Get-AADToken.ps1 (DeviceCode or RefreshToken flow)
    2. Updates launchSettings.json with the new token
    3. Rebuilds the agent (dotnet build)
    4. Ensures the dev tunnel (agent-graph-tunnel) is running
    5. Kills any process blocking port 3978
    6. Starts the agent
    7. Recovers existing Graph subscriptions (survives restarts/mailbox moves), falls back to creating a new one, then cleans up duplicates

.PARAMETER TenantId
    Azure AD tenant ID (required). Your Azure AD / Entra ID tenant GUID.

.PARAMETER ClientId
    Azure AD app client ID (required). The app registration used for bearer token auth.

.PARAMETER SkipTokenFetch
    Skip fetching a new token (use existing token in launchSettings.json).

.PARAMETER SkipBuild
    Skip dotnet build step.

.PARAMETER UseRefreshToken
    Use saved refresh token instead of DeviceCode flow (faster, no user interaction).

.EXAMPLE
    .\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>"

.EXAMPLE
    .\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>" -UseRefreshToken

.EXAMPLE
    .\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>" -UseRefreshToken -SkipBuild

.EXAMPLE
    .\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>" -SkipTokenFetch -SkipBuild
#>

param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$ClientId,
    [switch]$SkipTokenFetch,
    [switch]$SkipBuild,
    [switch]$UseRefreshToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$AgentDir        = $PSScriptRoot
$LaunchSettings  = Join-Path $AgentDir "Properties\launchSettings.json"
$GetTokenScript  = Join-Path $PSScriptRoot "scripts\Auth\Get-AADToken.ps1"
$DevTunnelName   = "agent-graph-tunnel"
$AgentPort       = 3978
$AgentUrl        = "http://localhost:$AgentPort"
$SubscribeUrl    = "$AgentUrl/api/graph/subscribe"
$RecoverUrl      = "$AgentUrl/api/graph/recover"
$CleanupUrl      = "$AgentUrl/api/graph/cleanup"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Write-Ok([string]$msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "    !!: $msg" -ForegroundColor Yellow }
function Write-Err([string]$msg)  { Write-Host "    ERROR: $msg" -ForegroundColor Red }

# ─────────────────────────────────────────────────────────────────────────────
# Step 1: Fetch bearer token
# ─────────────────────────────────────────────────────────────────────────────
if (-not $SkipTokenFetch) {
    Write-Step "Fetching bearer token"

    if (-not (Test-Path $GetTokenScript)) {
        Write-Err "Get-AADToken.ps1 not found at: $GetTokenScript"
        exit 1
    }

    $flowType = if ($UseRefreshToken) { "RefreshToken" } else { "DeviceCode" }
    Write-Host "    Flow: $flowType" -ForegroundColor Gray

    $tokenArgs = @{
        ClientId         = $ClientId
        TenantId         = $TenantId
        Environment      = "Test"
        FlowType         = $flowType
        SaveRefreshToken = (-not $UseRefreshToken)  # save on first DeviceCode run
    }

    # Dot-source so $token is available in this scope (Get-AADToken.ps1 assigns
    # to $token internally but never outputs it to the pipeline).
    . $GetTokenScript @tokenArgs

    if (-not $token -or -not $token.access_token) {
        Write-Err "Failed to acquire token. Aborting."
        exit 1
    }

    $bearerToken = $token.access_token
    Write-Ok "Token acquired (expires in $($token.expires_in)s)"

    # Update launchSettings.json
    Write-Step "Updating launchSettings.json"
    $settings = Get-Content $LaunchSettings -Raw | ConvertFrom-Json
    $settings.profiles.'Sample Agent with Bearer Token Support'.environmentVariables.BEARER_TOKEN = $bearerToken
    $settings | ConvertTo-Json -Depth 10 | Set-Content $LaunchSettings -Encoding UTF8
    Write-Ok "Token written to launchSettings.json"
}
else {
    Write-Warn "Skipping token fetch (--SkipTokenFetch)"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 2: Build the agent
# ─────────────────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Step "Building the agent (dotnet build)"
    Set-Location $AgentDir
    dotnet build --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Build failed. Aborting."
        exit 1
    }
    Write-Ok "Build succeeded"
}
else {
    Write-Warn "Skipping build (--SkipBuild)"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 3: Ensure dev tunnel is running
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Checking dev tunnel: $DevTunnelName"

$tunnelInfo = devtunnel show $DevTunnelName 2>&1
$hostConnections = ($tunnelInfo | Select-String "Host connections\s*:\s*(\d+)" | ForEach-Object { $_.Matches[0].Groups[1].Value })

if ($hostConnections -eq "0" -or -not $hostConnections) {
    Write-Warn "Dev tunnel is not hosting. Starting it in the background..."
    Start-Process -FilePath "devtunnel" -ArgumentList "host $DevTunnelName" -WindowStyle Minimized
    Start-Sleep -Seconds 3
    Write-Ok "Dev tunnel started ($DevTunnelName)"
}
else {
    Write-Ok "Dev tunnel already running ($hostConnections host connection(s))"
}

# ─────────────────────────────────────────────────────────────────────────────
# Step 4: Kill anything on port 3978
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Freeing port $AgentPort"

$netstatOutput = netstat -ano 2>$null | Select-String ":$AgentPort\s"
$pids = $netstatOutput |
        ForEach-Object { ($_ -split '\s+')[-1] } |
        Where-Object { $_ -match '^\d+$' } |
        Select-Object -Unique

foreach ($p in $pids) {
    Stop-Process -Id ([int]$p) -Force -ErrorAction SilentlyContinue
    Write-Ok "Killed process $p"
}

# Also kill any named agent processes
Get-Process -Name "SemanticKernelSampleAgent" -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill()
    Write-Ok "Killed SemanticKernelSampleAgent PID $($_.Id)"
}

Start-Sleep -Seconds 1

# ─────────────────────────────────────────────────────────────────────────────
# Step 5: Start the agent in background
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Starting the agent"

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:BEARER_TOKEN = (Get-Content $LaunchSettings -Raw | ConvertFrom-Json).profiles.'Sample Agent with Bearer Token Support'.environmentVariables.BEARER_TOKEN

Set-Location $AgentDir
$agentJob = Start-Job -ScriptBlock {
    param($dir)
    Set-Location $dir
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    dotnet run --project SemanticKernelSampleAgent.csproj --launch-profile "Sample Agent with Bearer Token Support" 2>&1
} -ArgumentList $AgentDir

Write-Host "    Waiting for agent to be ready..." -ForegroundColor Gray

$ready = $false
$timeout = 30
$elapsed = 0
while (-not $ready -and $elapsed -lt $timeout) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    $output = Receive-Job -Job $agentJob -Keep 2>&1
    if ($output -match "Now listening on") {
        $ready = $true
    }
}

if (-not $ready) {
    Write-Err "Agent did not start within $timeout seconds. Check logs:"
    Receive-Job -Job $agentJob | Write-Host
    exit 1
}

Write-Ok "Agent listening on $AgentUrl"

# ─────────────────────────────────────────────────────────────────────────────
# Step 6: Start Agents Playground and wait for a message
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Starting Agents Playground"

$playgroundProc = Get-Process -Name "agentsplayground" -ErrorAction SilentlyContinue
if ($playgroundProc) {
    Write-Warn "Agents Playground already running (PID $($playgroundProc.Id)). Restarting..."
    $playgroundProc | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 1
}
Start-Process "agentsplayground" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Ok "Agents Playground launched"

Write-Host ""
Write-Host "    ACTION REQUIRED:" -ForegroundColor Yellow
Write-Host "    Send at least one message to the agent in Agents Playground," -ForegroundColor Yellow
Write-Host "    then press ENTER here to register the Graph subscription." -ForegroundColor Yellow
Write-Host ""
Read-Host "    Press ENTER when ready"

# ─────────────────────────────────────────────────────────────────────────────
# Step 7: Register Graph subscription
# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Registering Graph mail subscription"

Start-Sleep -Seconds 1

# Try recovering existing subscriptions first (survives mailbox moves / restarts)
$subscriptionActive = $false
try {
    $recoverResponse = Invoke-RestMethod -Uri $RecoverUrl -Method Get -ErrorAction Stop
    if ($recoverResponse.recovered -gt 0) {
        Write-Ok "Recovered $($recoverResponse.recovered) existing subscription(s) from Graph"
        $subscriptionActive = $true
    }
    else {
        Write-Warn "No existing subscriptions found in Graph. Will try to create one."
    }
}
catch {
    Write-Warn "Recover failed: $($_.ErrorDetails.Message). Will try to create a new subscription."
}

# If no existing subscriptions found, create a new one
if (-not $subscriptionActive) {
    try {
        $subscribeResponse = Invoke-RestMethod -Uri $SubscribeUrl -Method Get -ErrorAction Stop
        Write-Ok "Subscription created: $($subscribeResponse.subscriptionId)"
        $subscriptionActive = $true
    }
    catch {
        $body = $_.ErrorDetails.Message
        if ($body -match "No conversation reference") {
            Write-Warn "No conversation reference found. Send a message from Agents Playground, then run:"
            Write-Warn "  Invoke-RestMethod -Uri '$SubscribeUrl' -Method Get"
        }
        else {
            Write-Err "Subscribe failed: $body"
        }
    }
}

# Clean up duplicate subscriptions (keep only the newest)
if ($subscriptionActive) {
    try {
        $cleanupResponse = Invoke-RestMethod -Uri $CleanupUrl -Method Get -ErrorAction Stop
        if ($cleanupResponse.deleted -gt 0) {
            Write-Ok "Cleaned up $($cleanupResponse.deleted) duplicate subscription(s). Keeping: $($cleanupResponse.kept)"
        }
        else {
            Write-Ok "No duplicate subscriptions to clean up"
        }
    }
    catch {
        Write-Warn "Cleanup failed (non-fatal): $($_.ErrorDetails.Message)"
    }
}

# ─────────────────────────────────────────────────────────────────────────────
# Done — stream agent logs to console
# ─────────────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Agent is running. Streaming logs below." -ForegroundColor Cyan
Write-Host "Press Ctrl+C to stop." -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

try {
    while ($true) {
        $output = Receive-Job -Job $agentJob
        if ($output) { $output | Write-Host }
        Start-Sleep -Milliseconds 500
    }
}
finally {
    Stop-Job -Job $agentJob -ErrorAction SilentlyContinue
    Remove-Job -Job $agentJob -ErrorAction SilentlyContinue
}
