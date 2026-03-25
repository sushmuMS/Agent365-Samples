# Agent Setup Guide

This guide covers setting up and running the Semantic Kernel sample agent with Graph mail notifications, trigger evaluation, and MCP tooling.

## Prerequisites

### Tools
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PowerShell 7+ (`winget install Microsoft.PowerShell`)
- [Microsoft Dev Tunnels CLI](https://learn.microsoft.com/azure/developer/dev-tunnels/get-started) (`winget install Microsoft.devtunnel`)

### Azure Resources
- **Azure OpenAI** resource with a deployed chat model (e.g., `gpt-4o`)
- **Azure AD / Entra ID** app registration with:
  - Application permissions: `Mail.Read`, `Mail.ReadWrite`, `Mail.Send`
  - Admin consent granted for those permissions
  - A client secret created

### MCP Platform (local)
The agent uses three MCP servers defined in `ToolingManifest.json`:

| Server | Role |
|--------|------|
| `mcp_TaskPersonalizationServerV2` | Trigger evaluation — decides which skills to run based on incoming events |
| `mcp_MailTools` | Email composition and sending tools |
| `mcp_WorkIQSandbox` | WorkIQ task management tools |

These servers must be running locally before starting the agent.

---

## One-Time Setup

### 1. Create a Dev Tunnel

```bash
devtunnel create agent-graph-tunnel --allow-anonymous
devtunnel port create agent-graph-tunnel -p 3978
```

Note the tunnel URL (e.g., `https://<id>.devtunnels.ms`). You will use it in the next step.

### 2. Configure `appsettings.json`

Copy the template and fill in your values:

```json
{
  "AzureOpenAI": {
    "Endpoint": "<<YOUR_AZURE_OPENAI_ENDPOINT>>",
    "ApiKey": "<<YOUR_AZURE_OPENAI_API_KEY>>",
    "DeploymentName": "<<YOUR_DEPLOYMENT_NAME>>"
  },
  "AzureAd": {
    "TenantId": "<<YOUR_TENANT_ID>>",
    "ClientId": "<<YOUR_CLIENT_ID>>"
  },
  "GraphSubscription": {
    "NotificationUrl": "<<YOUR_DEV_TUNNEL_URL>>/api/graph/notifications",
    "UserMailbox": "<<YOUR_MONITORED_MAILBOX_EMAIL>>",
    "ChangeType": "created",
    "Resource": "me/messages",
    "ExpirationHours": 1
  }
}
```

> **Never commit real secrets to `appsettings.json`.** Store the client secret using dotnet user-secrets (see below).

### 3. Store the Client Secret

```bash
cd dotnet/semantic-kernel/sample-agent
dotnet user-secrets set "AzureAd:ClientSecret" "<<YOUR_CLIENT_SECRET>>"
```

---

## Starting the Agent

Use `start-agent.ps1` to automate the full startup sequence. The script:

1. Acquires a bearer token (DeviceCode or saved refresh token) and injects it into `launchSettings.json`
2. Runs `dotnet build`
3. Checks the Dev Tunnel (`agent-graph-tunnel`) and starts it if not running
4. Kills any process occupying port 3978
5. Starts the agent and waits for it to be ready
6. Launches Agents Playground
7. Prompts you to send one message (to register the conversation reference), then auto-subscribes to Graph mail notifications

### First run (device code authentication)

```powershell
.\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>"
```

This opens a browser for device code authentication and saves a refresh token locally.

### Subsequent runs (faster, no browser prompt)

```powershell
.\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>" -UseRefreshToken
```

### Fast restart (skip token fetch and build)

```powershell
.\start-agent.ps1 -TenantId "<<YOUR_TENANT_ID>>" -ClientId "<<YOUR_CLIENT_ID>>" -SkipTokenFetch -SkipBuild
```

---

## Graph Subscription Management (Manual)

If you need to manage the Graph mail subscription without the startup script:

```bash
# Recover existing subscriptions after a restart (preferred — avoids duplicates)
curl http://localhost:3978/api/graph/recover

# Create a new subscription (requires at least one prior message to Agents Playground)
curl http://localhost:3978/api/graph/subscribe

# Remove duplicate subscriptions, keeping the newest
curl http://localhost:3978/api/graph/cleanup
```

> These endpoints are unauthenticated in development mode. Restrict or remove them before production deployment.

---

## Testing

1. Start the agent using the script above
2. Send at least one message to the agent in **Agents Playground** to establish a conversation reference
3. Press **Enter** in the terminal when prompted — the script will subscribe to Graph notifications
4. Send a test email to the monitored mailbox (`GraphSubscription:UserMailbox`)
5. The agent should receive a Graph change notification and respond in Agents Playground

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| "No conversation reference" on subscribe | Send a message to the agent in Agents Playground first, then retry `subscribe` |
| Dev tunnel not forwarding | Run `devtunnel host agent-graph-tunnel` manually |
| Port 3978 already in use | The script handles this automatically; manually: `netstat -ano \| findstr :3978` then `taskkill /PID <pid> /F` |
| Token expired mid-session | Rerun the script with `-UseRefreshToken` |
| Graph subscription expiring | Subscriptions auto-renew hourly; check `SubscriptionRenewalService` logs |
