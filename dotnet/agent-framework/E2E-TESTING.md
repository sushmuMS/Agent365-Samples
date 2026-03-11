# End-to-End Testing Guide: sample-agent + platform-agent

This guide covers the complete local dev setup for testing the two-agent system in Microsoft Teams:

- **sample-agent** — Teams-facing user agent (port 3978). Handles direct user messages, calls platform-agent for delegated tasks.
- **platform-agent** — Agentic-only shared capability hub (port 3979). Accepts only agentic (machine-to-machine) requests from other agents.

---

## Architecture

```
Teams User
    │
    ▼
sample-agent  (port 3978, Bot ID: <SAMPLE_BOT_ID>)
    │  agentic call
    ▼
platform-agent  (port 3979, Bot ID: <PLATFORM_BOT_ID>)
    │
    ▼
MCP Tools (Graph, email, calendar, …)
```

Each agent has its own Azure Bot registration and its own dev tunnel URL.

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| .NET 8 SDK | `dotnet --version` |
| `devtunnel` CLI | `devtunnel --version` — install from [aka.ms/devtunnel](https://aka.ms/devtunnel) |
| Two Azure Bot registrations | One for sample-agent, one for platform-agent |
| Azure OpenAI resource | Endpoint + API key + deployment name |
| Teams tenant | Admin-enabled sideloading, or a dev tenant |

---

## Part 1 — One-time Setup

### 1.1 Register two Azure Bots

In [Teams Dev Portal → Bot Management](https://dev.teams.microsoft.com/tools/bots):

1. Create **sample-agent bot** → note `ClientId` and create a `ClientSecret`.
2. Create **platform-agent bot** → note `ClientId` and create a `ClientSecret`.

Both bots should use:
- **AuthType**: Client Secret
- **Scopes**: `https://api.botframework.com/.default`

### 1.2 Create dev tunnels

```bash
# Login (one-time)
devtunnel user login

# Create and configure tunnel for sample-agent (port 3978)
devtunnel create --allow-anonymous
devtunnel port create <tunnel-id> -p 3978 --protocol https
devtunnel host <tunnel-id>
# → note the URL, e.g. https://svsrn354-3978.usw2.devtunnels.ms

# Create and configure tunnel for platform-agent (port 3979)
devtunnel create --allow-anonymous
devtunnel port create <tunnel-id> -p 3979 --protocol https
devtunnel host <tunnel-id>
# → note the URL, e.g. https://svsrn354-3979.usw2.devtunnels.ms
```

> **Tip:** Run each `devtunnel host` in a separate terminal so both stay alive.

---

## Part 2 — Configure sample-agent

### 2a. Automated (recommended)

Run the setup script — it updates both appsettings files, rebuilds the manifest zip, and restarts the agent:

```powershell
cd dotnet\agent-framework\scripts   # or repo root scripts\
.\setup-teams-dev.ps1 `
  -BotId        "<SAMPLE_CLIENT_ID>" `
  -ClientSecret "<SAMPLE_CLIENT_SECRET>" `
  -TunnelUrl    "https://<sample-tunnel-url>" `
  -TenantId     "<TENANT_ID>" `
  -AzureOpenAIKey "<AZURE_OPENAI_KEY>"
```

Script actions:
1. Updates `TokenValidation.Audiences` and `ServiceConnection.ClientId/ClientSecret` in `appsettings.json`
2. Updates `appsettings.Playground.json` with the same values
3. Rebuilds `appPackage/AgentSample-teamsapp.zip` with the tunnel domain and bot ID injected into `manifest.json`
4. Restarts the agent process (`dotnet run --launch-profile AgentFrameworkSampleAgent`)

### 2b. Manual

Edit `dotnet/agent-framework/sample-agent/appsettings.json`:

```json
{
  "TokenValidation": {
    "Audiences": ["<SAMPLE_CLIENT_ID>"]
  },
  "Connections": {
    "ServiceConnection": {
      "Settings": {
        "AuthType": "ClientSecret",
        "ClientId": "<SAMPLE_CLIENT_ID>",
        "ClientSecret": "<SAMPLE_CLIENT_SECRET>",
        "AuthorityEndpoint": "https://login.microsoftonline.com/<TENANT_ID>",
        "Scopes": ["https://api.botframework.com/.default"]
      }
    }
  },
  "AIServices": {
    "AzureOpenAI": {
      "DeploymentName": "gpt-4o",
      "Endpoint": "<AZURE_OPENAI_ENDPOINT>",
      "ApiKey": "<AZURE_OPENAI_KEY>"
    }
  }
}
```

---

## Part 3 — Configure platform-agent

Edit `dotnet/agent-framework/platform-agent/appsettings.json`:

```json
{
  "TokenValidation": {
    "Audiences": ["<PLATFORM_CLIENT_ID>"]
  },
  "Connections": {
    "ServiceConnection": {
      "Settings": {
        "AuthType": "ClientSecret",
        "ClientId": "<PLATFORM_CLIENT_ID>",
        "ClientSecret": "<PLATFORM_CLIENT_SECRET>",
        "AuthorityEndpoint": "https://login.microsoftonline.com/<TENANT_ID>",
        "Scopes": ["https://api.botframework.com/.default"]
      }
    }
  },
  "AIServices": {
    "AzureOpenAI": {
      "DeploymentName": "gpt-4o",
      "Endpoint": "<AZURE_OPENAI_ENDPOINT>",
      "ApiKey": "<AZURE_OPENAI_KEY>"
    }
  }
}
```

For local Playground testing without Teams auth, edit `appsettings.Playground.json` and set:

```json
{
  "TokenValidation": {
    "Enabled": false
  }
}
```

---

## Part 4 — Build and run both agents

```bash
# Terminal 1 — sample-agent (port 3978)
cd dotnet/agent-framework/sample-agent
dotnet run --launch-profile AgentFrameworkSampleAgent

# Terminal 2 — platform-agent (port 3979)
cd dotnet/agent-framework/platform-agent
dotnet run --launch-profile platform-agent
```

Verify both are healthy:

```bash
curl http://localhost:3978/api/health   # → {"status":"healthy",...}
curl http://localhost:3979/api/health   # → {"status":"healthy",...}
```

---

## Part 5 — Wire up Teams

### 5.1 Update bot messaging endpoints

In [Teams Dev Portal → Bot Management](https://dev.teams.microsoft.com/tools/bots):

| Bot | Messaging endpoint |
|-----|--------------------|
| sample-agent | `https://<sample-tunnel-url>/api/messages` |
| platform-agent | `https://<platform-tunnel-url>/api/messages` |

### 5.2 Upload the Teams app

1. Teams → **Apps** → **Manage your apps** → **Upload a custom app**
2. Select: `dotnet/agent-framework/sample-agent/appPackage/AgentSample-teamsapp.zip`
3. If already installed: click **...** → **Update**

> The `AgentSample-teamsapp.zip` is rebuilt by the setup script (Part 2a). If running manually, update `appPackage/manifest.json` with the correct `botId` and `validDomains` before zipping.

---

## Part 6 — Test

1. Open the **AgentFrameworkSample** app in Teams.
2. Send: `What's the weather in Seattle?`
3. Expected: streaming response with current weather.
4. Send a task that triggers platform-agent delegation (e.g. an email or calendar query, depending on configured MCP tools).

Check logs:
- sample-agent: `POST /api/messages → 202`
- platform-agent: `POST /api/messages → 202` (called by sample-agent)

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `MsalUserAuthorization not found` | `"me"` handler active | Comment out the `"me"` handler block in `appsettings.json` |
| `HTTP 401` from CloudAdapter | Wrong `AuthType` or `Scopes` | Set `AuthType: ClientSecret`, `Scopes: https://api.botframework.com/.default` |
| `HTTP 401` from Azure OpenAI | Expired API key | Rotate in Azure Portal, update `appsettings.json` |
| `HTTP/2 stream reset` (MSAL) | .NET HTTP/2 incompatibility | `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2Support", false)` at top of `Program.cs` — already applied in `Development` |
| No response in Teams | Tunnel not public | `devtunnel port create` with `--allow-anonymous`; verify `devtunnel host` is running |
| platform-agent not called | sample-agent not configured with platform-agent URL | Ensure sample-agent `appsettings.json` has platform-agent's `ServiceUrl` in `ConnectionsMap` or tool config |
| `Invalid BotId` on Teams upload | Bot not registered in Bot Framework | Create bot in Teams Dev Portal → Bot Management first |
| Port 3979 not reachable | platform-agent tunnel not started | Start second `devtunnel host` for port 3979 |

---

## Quick Reference — Per-Developer Values

> Each developer fills in their own values. Tunnels expire after 30 days — recreate and update when needed.
> **Do not commit personal values to source control.** Keep them in your local `appsettings.json` / launch config only.

| Parameter | Where to get it | Your value |
|-----------|-----------------|------------|
| Tenant ID | Azure Portal → Azure Active Directory → Overview → Tenant ID | `<<YOUR_TENANT_ID>>` |
| sample-agent Bot/Client ID | Teams Dev Portal → Bot Management → your sample-agent bot | `<<SAMPLE_BOT_CLIENT_ID>>` |
| sample-agent Client Secret | Teams Dev Portal → Bot Management → your sample-agent bot → Client Secrets | *(store in user secrets / env only)* |
| sample-agent tunnel URL | `devtunnel host <tunnel-id>` output (port 3978) | `https://<<your-tunnel-id>>-3978.usw2.devtunnels.ms` |
| platform-agent Bot/Client ID | Teams Dev Portal → Bot Management → your platform-agent bot | `<<PLATFORM_BOT_CLIENT_ID>>` |
| platform-agent Client Secret | Teams Dev Portal → Bot Management → your platform-agent bot → Client Secrets | *(store in user secrets / env only)* |
| platform-agent tunnel URL | `devtunnel host <tunnel-id>` output (port 3979) | `https://<<your-tunnel-id>>-3979.usw2.devtunnels.ms` |
| Azure OpenAI endpoint | Azure Portal → your Azure OpenAI resource → Keys and Endpoint | `https://<<your-resource>>.cognitiveservices.azure.com/` |
| Azure OpenAI API Key | Azure Portal → your Azure OpenAI resource → Keys and Endpoint | *(store in user secrets / env only)* |
| Azure OpenAI deployment | Azure OpenAI Studio → Deployments | `gpt-4o` *(or your deployment name)* |

---

## Appendix A — VS Code Dev Tunnels Setup

VS Code has built-in support for dev tunnels via the **Ports** panel, which is the easiest way to set up tunnels without the CLI.

### Option 1: VS Code Port Forwarding (Recommended for dev)

1. Open VS Code and sign in with your Microsoft account (`Ctrl+Shift+P` → **"Remote Tunnels: Sign in"**).

2. Start your agents (see Part 4).

3. Open the **Ports** panel:
   - Bottom panel → **PORTS** tab, or
   - `Ctrl+Shift+P` → **"Forward a Port"**

4. Forward port **3978** (sample-agent):
   - Click **Forward a Port** → enter `3978`
   - Right-click the forwarded port → **Port Visibility** → **Public**
   - Copy the tunnel URL (e.g. `https://abc123-3978.usw2.devtunnels.ms`)

5. Repeat for port **3979** (platform-agent).

6. Use these URLs as your `BotEndpoint` / `validDomains` values.

> **Tip:** VS Code tunnels restart automatically when you re-open the workspace. The tunnel URL stays stable as long as you keep the same VS Code session.

### Option 2: devtunnel CLI

```bash
# One-time login
devtunnel user login

# Create persistent tunnels (URLs survive host restarts)
devtunnel create --allow-anonymous
devtunnel port create <tunnel-id> -p 3978 --protocol https
devtunnel host <tunnel-id>   # Keep this terminal open

# In a second terminal for platform-agent
devtunnel create --allow-anonymous
devtunnel port create <tunnel-id> -p 3979 --protocol https
devtunnel host <tunnel-id>   # Keep this terminal open
```

> Tunnel IDs survive restarts; only the access token changes. Use `devtunnel list` to see your existing tunnels.

### VS Code launch.json for both agents

Add the following to `.vscode/launch.json` (create it if it doesn't exist). Replace placeholders with your actual values:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "sample-agent",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build-sample-agent",
      "program": "${workspaceFolder}/dotnet/agent-framework/sample-agent/bin/Debug/net8.0/AgentFrameworkSampleAgent.dll",
      "args": [],
      "cwd": "${workspaceFolder}/dotnet/agent-framework/sample-agent",
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": "http://localhost:3978"
      },
      "launchBrowser": false,
      "serverReadyAction": {
        "action": "noBrowser",
        "pattern": "Now listening on: \\S+"
      }
    },
    {
      "name": "platform-agent",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build-platform-agent",
      "program": "${workspaceFolder}/dotnet/agent-framework/platform-agent/bin/Debug/net8.0/platform-agent.dll",
      "args": [],
      "cwd": "${workspaceFolder}/dotnet/agent-framework/platform-agent",
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": "http://localhost:3979"
      },
      "launchBrowser": false
    }
  ],
  "compounds": [
    {
      "name": "Both agents",
      "configurations": ["sample-agent", "platform-agent"]
    }
  ]
}
```

Add corresponding tasks to `.vscode/tasks.json`:

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "build-sample-agent",
      "command": "dotnet",
      "type": "process",
      "args": ["build", "${workspaceFolder}/dotnet/agent-framework/sample-agent/AgentFrameworkSampleAgent.csproj"],
      "problemMatcher": "$msCompile"
    },
    {
      "label": "build-platform-agent",
      "command": "dotnet",
      "type": "process",
      "args": ["build", "${workspaceFolder}/dotnet/agent-framework/platform-agent/platform-agent.csproj"],
      "problemMatcher": "$msCompile"
    }
  ]
}
```

---

## Appendix B — Local-Only Files (Not Committed)

The following files exist locally but are **excluded from source control** because they contain developer-specific credentials or generated artifacts:

| File | Reason | How to recreate |
|------|--------|-----------------|
| `sample-agent/scripts/Get-McpUserToken.ps1` | Contains your personal `ClientId`, `TenantId`, and acquired tokens | Copy from the template below and fill in your values |
| `sample-agent/appPackage/teamsapp-extracted/manifest.json` | Generated by extracting the zip; contains your specific bot ID and tunnel domain | Regenerate by unzipping `AgentSample-teamsapp.zip` after running the setup script |
| `sample-agent/appPackage/AgentSample-teamsapp.zip` | Packed Teams app with your dev bot ID and tunnel domain baked in | Regenerate by running the setup script (Part 2a) or manually zip the `appPackage/teamsapp-extracted/` folder |
| `sample-agent/appsettings.json` | Contains your ClientId, ClientSecret, Azure OpenAI key | Copy from `appsettings.json` example in Part 2b and fill in your values |
| `sample-agent/appsettings.Playground.json` | Contains your local Playground overrides | Copy and adjust as needed |
| `platform-agent/appsettings.json` | Contains your ClientId, ClientSecret, Azure OpenAI key | Copy from `appsettings.json` example in Part 3 and fill in your values |

### Get-McpUserToken.ps1 Template

To recreate the MCP user token script locally, create `sample-agent/scripts/Get-McpUserToken.ps1` with:

```powershell
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Gets a delegated user token for the MCP service.
# Usage: .\Get-McpUserToken.ps1 -ClientId "<your-client-id>" -TenantId "<your-tenant-id>"

param(
    [Parameter(Mandatory=$true)]
    [string]$ClientId,
    [Parameter(Mandatory=$true)]
    [string]$TenantId,
    [string]$RedirectUri = "http://localhost:9999",
    [int]   $Port        = 9999
)

$Scopes = "05879165-0320-489e-b644-f72b33f3edf0/McpServers.Mail.All " +
          "05879165-0320-489e-b644-f72b33f3edf0/McpServers.Calendar.All " +
          "offline_access"

$encodedRedirect = [uri]::EscapeDataString($RedirectUri)
$encodedScopes   = [uri]::EscapeDataString($Scopes)

$authUrl = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/authorize" +
           "?client_id=$ClientId" +
           "&response_type=code" +
           "&redirect_uri=$encodedRedirect" +
           "&scope=$encodedScopes" +
           "&prompt=select_account"

Write-Host "Starting local listener on port $Port..." -ForegroundColor Cyan
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()

Write-Host "Opening browser for sign-in..." -ForegroundColor Cyan
Start-Process $authUrl
Write-Host "Waiting for redirect..." -ForegroundColor Yellow

$context = $listener.GetContext()
$request = $context.Request
$code    = $request.QueryString["code"]
$error   = $request.QueryString["error"]

$html = if ($code) { "<html><body><h2>Authentication complete. You can close this window.</h2></body></html>" } `
        else       { "<html><body><h2>Authentication failed: $error</h2></body></html>" }
$buf = [System.Text.Encoding]::UTF8.GetBytes($html)
$context.Response.ContentLength64 = $buf.Length
$context.Response.OutputStream.Write($buf, 0, $buf.Length)
$context.Response.OutputStream.Close()
$listener.Stop()

if ($error -or -not $code) { Write-Error "Auth error: $error"; exit 1 }

Write-Host "Exchanging code for token..." -ForegroundColor Cyan
$tokenResponse = Invoke-RestMethod `
    -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -Method POST `
    -Body @{ client_id=$ClientId; code=$code; redirect_uri=$RedirectUri; grant_type="authorization_code" } `
    -ContentType "application/x-www-form-urlencoded"

$accessToken = $tokenResponse.access_token
Write-Host "SUCCESS! Token expires in $($tokenResponse.expires_in)s." -ForegroundColor Green
Write-Host 'Add to launch.json: "MCP_USER_TOKEN": "' -NoNewline
Write-Host $accessToken -NoNewline
Write-Host '"'
try { $accessToken | Set-Clipboard; Write-Host "(Copied to clipboard)" -ForegroundColor DarkGray } catch {}
```

> **Usage:** `.\Get-McpUserToken.ps1 -ClientId "<<SAMPLE_BOT_CLIENT_ID>>" -TenantId "<<YOUR_TENANT_ID>>"`
