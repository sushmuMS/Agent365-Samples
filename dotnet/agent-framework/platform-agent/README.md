# platform-agent — Shared Capability Hub

The **platform-agent** is an agentic-only shared capability hub in the two-agent Teams architecture. It runs on port 3979 and is called by other agents (like `sample-agent`) via machine-to-machine requests. It exposes MCP tools (mail, calendar, Teams, SharePoint, etc.) and does not accept direct user messages.

---

## What This Sample Demonstrates

- **Agent-to-agent delegation**: how a calling agent (sample-agent) delegates tasks to a shared capability hub
- **Agentic-only endpoint**: validating requests come from other agents (`IsAgenticRequest()`) rather than end users
- **MCP tooling via ToolingManifest**: loading and invoking MCP tool servers from a manifest at runtime
- **OpenTelemetry instrumentation**: full baggage propagation (tenant, agent, conversation IDs) and span tracking for agent operations
- **Azure Bot authentication**: client-credentials flow using `AuthType: ClientSecret`

---

## Architecture

```
Teams User
    │
    ▼
sample-agent  (port 3978)
    │  agentic call (machine-to-machine)
    ▼
platform-agent  (port 3979)
    │
    ▼
MCP Tools (Graph, mail, calendar, Teams, SharePoint, …)
```

See [E2E-TESTING.md](../E2E-TESTING.md) for the full two-agent local setup guide.

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| .NET 8 SDK | `dotnet --version` |
| Azure Bot registration | Client ID + Client Secret for this agent |
| Azure OpenAI resource | Endpoint, API key, and deployment name |
| Local MCP server (optional) | For `TOOLS_MODE=MockMCPServer` — see [MCP-USER-AUTH.md](../MCP-USER-AUTH.md) |

---

## Configuration

Copy `appsettings.json` values and fill in your credentials. Never commit real secrets — use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) or environment variables.

```json
{
  "TokenValidation": {
    "Audiences": ["<<PLATFORM_BOT_CLIENT_ID>>"]
  },
  "Connections": {
    "ServiceConnection": {
      "Settings": {
        "AuthType": "ClientSecret",
        "ClientId": "<<PLATFORM_BOT_CLIENT_ID>>",
        "ClientSecret": "<<PLATFORM_BOT_CLIENT_SECRET>>",
        "AuthorityEndpoint": "https://login.microsoftonline.com/<<YOUR_TENANT_ID>>",
        "Scopes": ["https://api.botframework.com/.default"]
      }
    }
  },
  "AIServices": {
    "AzureOpenAI": {
      "DeploymentName": "gpt-4o",
      "Endpoint": "<<YOUR_AZURE_OPENAI_ENDPOINT>>",
      "ApiKey": "<<YOUR_AZURE_OPENAI_API_KEY>>"
    }
  }
}
```

### ToolingManifest.json

Update `ToolingManifest.json` with your tenant ID to point to your local MCP server:

```json
{
  "mcpServers": [
    {
      "mcpServerName": "mcp_MailTools",
      "url": "http://localhost:52857/agents/tenants/<<YOUR_TENANT_ID>>/servers/mcp_MailTools"
    }
  ]
}
```

### Playground (no auth)

For local testing without Teams JWT validation, create/edit `appsettings.Playground.json`:

```json
{
  "TokenValidation": {
    "Enabled": false
  }
}
```

---

## Running

```bash
# From repo root
cd dotnet/agent-framework/platform-agent
dotnet run --launch-profile platform-agent
```

Or in VS Code, use the **"platform-agent"** launch configuration (see [E2E-TESTING.md Appendix A](../E2E-TESTING.md#appendix-a--vs-code-dev-tunnels-setup)).

Verify the agent is running:

```bash
curl http://localhost:3979/api/health
# → {"status":"healthy","timestamp":"..."}
```

---

## Testing

### With sample-agent (two-agent end-to-end)

Follow the full setup in [E2E-TESTING.md](../E2E-TESTING.md). Run both agents, wire up Teams dev tunnels, and send a message to sample-agent that triggers delegation to platform-agent.

### Standalone (Playground mode)

Use the [Agent 365 Playground](https://playground.agent365.microsoft.com) or any HTTP client to POST directly to `/api/messages` with `appsettings.Playground.json` active (token validation disabled).

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `HTTP 401` on startup | Wrong `AuthType` or `Scopes` | Set `AuthType: ClientSecret`, `Scopes: https://api.botframework.com/.default` |
| `HTTP 401` from Azure OpenAI | Expired or wrong API key | Rotate in Azure Portal, update config |
| `HTTP/2 stream reset` (MSAL) | .NET HTTP/2 incompatibility | Already disabled in `Development` via `AppContext.SetSwitch` in `Program.cs:27` |
| MCP tools not loading | Wrong tenant ID in `ToolingManifest.json` | Replace `<<YOUR_TENANT_ID>>` with your actual tenant GUID |
| Port 3979 already in use | Another process on 3979 | Kill the conflicting process or change the port in `Program.cs` |
| sample-agent not calling platform-agent | sample-agent not configured with platform-agent URL | Ensure sample-agent config has the platform-agent tunnel URL in its agentic tool config |
