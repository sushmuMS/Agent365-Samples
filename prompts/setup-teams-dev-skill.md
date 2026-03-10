# Skill: Setup Agent Framework for Teams Dev Testing

## Purpose
Automate the end-to-end setup for testing the dotnet Agent Framework sample agent
in Microsoft Teams via a local dev tunnel.

## Trigger
Use this skill when the user says any of:
- "set up Teams testing"
- "configure for Teams"
- "help me test in Teams"
- "run setup-teams-dev"

## Prerequisites to Collect
Before running, gather these from the user if not already known:

| Parameter | Where to find | Example |
|-----------|--------------|---------|
| Bot ID | Teams Dev Portal -> Bot Management -> Configure | `0a5512fa-...` |
| Client Secret | Teams Dev Portal -> Bot Management -> Client Secrets | `Gde8Q~...` |
| Dev Tunnel URL | VS Code Ports panel (port 3978, Public visibility) | `https://sc1xm1g7-3978.usw2.devtunnels.ms` |
| Azure OpenAI Key | Azure Portal -> OpenAI resource -> Keys and Endpoint | `FXjRg...` |

## Steps

### 1. Verify dev tunnel is active and public
```bash
curl.exe -s https://<tunnel-url>/api/health
```
If not reachable, ensure port 3978 is forwarded in VS Code Ports panel with Public visibility.

### 2. Run the setup script
```powershell
cd d:\workiqAgent\Agent365-Samples\scripts
.\setup-teams-dev.ps1 `
  -BotId "<bot-id>" `
  -ClientSecret "<client-secret>" `
  -TunnelUrl "https://<tunnel-url>" `
  -AzureOpenAIKey "<openai-key>"
```

### 3. Update bot endpoint in Teams Dev Portal
- Go to: https://dev.teams.microsoft.com/tools/bots/<bot-id>/configure
- Set Bot endpoint URL to: `https://<tunnel-url>/api/messages`
- Save

### 4. Upload or update the Teams app
- Teams -> Apps -> Manage your apps -> Upload a custom app
- File: `dotnet/agent-framework/sample-agent/appPackage/AgentSample-teamsapp.zip`
- If already installed: update via "..." menu -> Update

### 5. Verify agent is responding
- Send "What's the weather in Seattle?" to the bot in Teams
- Check agent logs for POST /api/messages with 202 response

## Common Issues

| Error | Cause | Fix |
|-------|-------|-----|
| `MsalUserAuthorization not found` | `"me"` handler active in appsettings.json | Comment out the `"me"` handler block |
| `HTTP 401` from CloudAdapter | Wrong scope or AuthType in ServiceConnection | Set `AuthType=ClientSecret`, `Scopes=https://api.botframework.com/.default` |
| `HTTP 401` from Azure OpenAI | Expired API key | Rotate key in Azure Portal, update appsettings |
| `HTTP/2 stream reset` (MSAL) | .NET HTTP/2 incompatibility with login.microsoftonline.com | Add `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2Support", false)` at top of Program.cs |
| `Invalid BotId` on Teams upload | Bot not registered with Bot Framework | Create bot in Teams Dev Portal -> Bot Management |
| No response in Teams | Agent not reachable via tunnel | Check VS Code Ports panel - port 3978 must be Public |

## Config File Reference

### appsettings.json (Development/Teams)
```json
{
  "TokenValidation": {
    "Audiences": ["<bot-id>"]
  },
  "Connections": {
    "ServiceConnection": {
      "Settings": {
        "AuthType": "ClientSecret",
        "ClientId": "<bot-id>",
        "ClientSecret": "<client-secret>",
        "AuthorityEndpoint": "https://login.microsoftonline.com/<tenant-id>",
        "Scopes": ["https://api.botframework.com/.default"]
      }
    }
  }
}
```

### appsettings.Playground.json (Agents Playground / local testing)
Same as above but also includes:
```json
{
  "TokenValidation": {
    "Enabled": false
  }
}
```

### launchSettings.json profiles
| Profile | Port | Environment | MCP Mode | Use for |
|---------|------|-------------|----------|---------|
| `AgentFrameworkSampleAgent` | 3978 | Development | Set via env | Teams testing |
| `Playground` | 3978 | Playground | MockMCPServer | Agents Playground / WebChat |
