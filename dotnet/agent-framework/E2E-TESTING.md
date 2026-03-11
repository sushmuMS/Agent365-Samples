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

## Quick Reference — Current Dev Values

> Update these when tunnels are recreated (tunnels expire after 30 days).

| Parameter | Value |
|-----------|-------|
| Tenant ID | `6e8b84fa-ae41-4a00-9ad1-934b73e5d73c` |
| sample-agent Bot/Client ID | `0a5512fa-7e4c-4e09-aef1-b23fd8ea7e9e` |
| sample-agent tunnel | `https://svsrn354-3978.usw2.devtunnels.ms` |
| sample-agent tunnel ID | `quick-fog-1t2tm5j.usw2` |
| platform-agent Bot/Client ID | `b63dcd87-e522-4989-9b2a-b81789524f6e` |
| platform-agent tunnel | `https://svsrn354-3979.usw2.devtunnels.ms` |
| Azure OpenAI endpoint | `https://aafraimian-3919-resource.cognitiveservices.azure.com/` |
| Azure OpenAI deployment | `gpt-4o` |
