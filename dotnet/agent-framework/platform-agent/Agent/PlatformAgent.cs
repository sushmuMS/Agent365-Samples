// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Agent365PlatformAgent.telemetry;
using Microsoft.Agents.A365.Observability.Caching;
using Microsoft.Agents.A365.Runtime.Utils;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Agents.A365.Tooling.Models;
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Agent365PlatformAgent.Agent
{
    public class PlatformAgent : AgentApplication
    {
        private readonly string AgentInstructions = """
            You are PlatformAgent, a shared capability service for other AI agents.
            You receive task descriptions from calling agents and execute them autonomously
            using your available tools (email, web search, summarization, calendar, Teams, SharePoint).

            RULES:
            - Decompose multi-step tasks into sequential tool calls. Do not ask for clarification.
            - For email workflows: read → summarize/analyze → act (send/reply) in that order.
            - For sentiment analysis: classify as "positive", "negative", or "neutral" with a
              confidence score between 0.0 and 1.0.
            - Always return a structured JSON response in this exact format:
              {
                "status": "completed" | "partial" | "failed",
                "results": { ... task-specific key/value pairs ... },
                "actionsTaken": ["tool_name_1", "tool_name_2"],
                "errors": ["optional error messages if any step failed"]
              }
            - If a capability is unavailable, set status to "partial" and document what succeeded
              and what did not in the errors array.
            - Never ask clarifying questions. Infer intent and complete the task.
            """;

        private readonly IChatClient? _chatClient;
        private readonly IConfiguration? _configuration;
        private readonly IExporterTokenCache<AgenticTokenStruct>? _agentTokenCache;
        private readonly ILogger<PlatformAgent>? _logger;
        private readonly IMcpToolRegistrationService? _toolService;
        private readonly IMcpToolServerConfigurationService? _toolServerConfigService;
        private readonly string? AgenticAuthHandlerName;
        private static readonly ConcurrentDictionary<string, List<AITool>> _agentToolCache = new();

        public static bool TryGetBearerTokenForDevelopment(out string? bearerToken)
        {
            bearerToken = Environment.GetEnvironmentVariable("BEARER_TOKEN");
            return !string.IsNullOrEmpty(bearerToken);
        }

        private static bool ShouldSkipToolingOnErrors()
        {
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var skip = Environment.GetEnvironmentVariable("SKIP_TOOLING_ON_ERRORS");
            return env.Equals("Development", StringComparison.OrdinalIgnoreCase) &&
                   "true".Equals(skip, StringComparison.OrdinalIgnoreCase);
        }

        public PlatformAgent(
            AgentApplicationOptions options,
            IChatClient chatClient,
            IConfiguration configuration,
            IExporterTokenCache<AgenticTokenStruct> agentTokenCache,
            IMcpToolRegistrationService toolService,
            IMcpToolServerConfigurationService toolServerConfigService,
            ILogger<PlatformAgent> logger) : base(options)
        {
            _chatClient = chatClient;
            _configuration = configuration;
            _agentTokenCache = agentTokenCache;
            _logger = logger;
            _toolService = toolService;
            _toolServerConfigService = toolServerConfigService;

            AgenticAuthHandlerName = _configuration.GetValue<string>("AgentApplication:AgenticAuthHandlerName");

            // AGENTIC-ONLY: PlatformAgent does not accept direct user requests.
            // Calling agents must present a valid agentic token.
            var agenticHandlers = !string.IsNullOrEmpty(AgenticAuthHandlerName)
                ? new[] { AgenticAuthHandlerName }
                : Array.Empty<string>();
            OnActivity(ActivityTypes.Message, OnMessageAsync, isAgenticOnly: true,
                autoSignInHandlers: agenticHandlers);
        }

        protected async Task OnMessageAsync(
            ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
        {
            // PlatformAgent only accepts agentic requests — always use agentic auth handler
            string? authHandlerName = AgenticAuthHandlerName;

            await A365OtelWrapper.InvokeObservedAgentOperation(
                "PlatformAgent.MessageProcessor",
                turnContext, turnState, _agentTokenCache,
                UserAuthorization, authHandlerName ?? string.Empty, _logger,
                async () =>
                {
                    await turnContext.StreamingResponse.QueueInformativeUpdateAsync(
                        "Processing request...").ConfigureAwait(false);
                    try
                    {
                        var userText = turnContext.Activity.Text?.Trim() ?? string.Empty;
                        var agent = await GetClientAgent(turnContext, turnState, _toolService, authHandlerName);

                        // Stateless: fresh thread per agentic request (no cross-call memory)
                        var thread = agent!.GetNewThread();

                        await foreach (var response in agent.RunStreamingAsync(
                            userText, thread, cancellationToken: cancellationToken))
                        {
                            if (response.Role == ChatRole.Assistant && !string.IsNullOrEmpty(response.Text))
                                turnContext?.StreamingResponse.QueueTextChunk(response.Text);
                        }
                        // Note: thread state is NOT saved — each agentic call is independent
                    }
                    finally
                    {
                        await turnContext.StreamingResponse.EndStreamAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                });
        }

        private async Task<AIAgent?> GetClientAgent(
            ITurnContext context, ITurnState turnState,
            IMcpToolRegistrationService? toolService, string? authHandlerName)
        {
            AssertionHelpers.ThrowIfNull(_configuration!, nameof(_configuration));
            AssertionHelpers.ThrowIfNull(context, nameof(context));
            AssertionHelpers.ThrowIfNull(_chatClient!, nameof(_chatClient));

            // PlatformAgent has NO local tools — all tools come from MCP servers
            var toolList = new List<AITool>();

            if (toolService != null)
            {
                try
                {
                    string toolCacheKey = GetToolCacheKey(turnState);
                    if (_agentToolCache.TryGetValue(toolCacheKey, out var cached) && cached?.Count > 0)
                    {
                        toolList.AddRange(cached);
                    }
                    else
                    {
                        await context.StreamingResponse.QueueInformativeUpdateAsync("Loading platform tools...");

                        if (!string.IsNullOrEmpty(authHandlerName))
                        {
                            string? agentId = Utility.ResolveAgentIdentity(context,
                                await UserAuthorization.GetTurnTokenAsync(context, authHandlerName));
                            if (!string.IsNullOrEmpty(agentId))
                            {
                                var mcpTools = await toolService.GetMcpToolsAsync(
                                    agentId, UserAuthorization, authHandlerName, context).ConfigureAwait(false);
                                if (mcpTools?.Count > 0)
                                {
                                    toolList.AddRange(mcpTools);
                                    _agentToolCache.TryAdd(toolCacheKey, [.. mcpTools]);
                                }
                            }
                        }
                        else if (IsLocalMcpMode() && _toolServerConfigService != null)
                        {
                            _logger?.LogInformation("TOOLS_MODE=MockMCPServer: loading tools from ToolingManifest.json");
                            var localTools = await LoadLocalMcpToolsAsync(context);
                            if (localTools.Count > 0)
                            {
                                toolList.AddRange(localTools);
                                _agentToolCache.TryAdd(toolCacheKey, [.. localTools]);
                            }
                        }
                        else if (TryGetBearerTokenForDevelopment(out var bearerToken))
                        {
                            _logger?.LogInformation("Using BEARER_TOKEN for MCP tools (development).");
                            string? agentId = Utility.ResolveAgentIdentity(context, bearerToken!);
                            if (!string.IsNullOrEmpty(agentId))
                            {
                                var mcpTools = await toolService.GetMcpToolsAsync(
                                    agentId, UserAuthorization, AgenticAuthHandlerName ?? string.Empty,
                                    context, bearerToken).ConfigureAwait(false);
                                if (mcpTools?.Count > 0)
                                {
                                    toolList.AddRange(mcpTools);
                                    _agentToolCache.TryAdd(toolCacheKey, [.. mcpTools]);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ShouldSkipToolingOnErrors())
                        _logger?.LogWarning(ex, "Failed to load MCP tools. Continuing without (SKIP_TOOLING_ON_ERRORS=true).");
                    else
                    {
                        _logger?.LogError(ex, "Failed to load MCP tools.");
                        throw;
                    }
                }
            }

            var toolOptions = new ChatOptions { Temperature = (float?)0.2, Tools = toolList };

            return new ChatClientAgent(_chatClient!,
                new ChatClientAgentOptions
                {
                    Instructions = AgentInstructions,
                    ChatOptions = toolOptions,
                    ChatMessageStoreFactory = ctx =>
                    {
#pragma warning disable MEAI001
                        return new InMemoryChatMessageStore(new MessageCountingChatReducer(10),
                            ctx.SerializedState, ctx.JsonSerializerOptions);
#pragma warning restore MEAI001
                    }
                })
                .AsBuilder()
                .UseOpenTelemetry(sourceName: AgentMetrics.SourceName, cfg => cfg.EnableSensitiveData = true)
                .Build();
        }

        private string GetToolCacheKey(ITurnState turnState)
        {
            string key = turnState.User.GetValue<string?>("user.toolCacheKey", () => null) ?? "";
            if (string.IsNullOrEmpty(key))
            {
                key = Guid.NewGuid().ToString();
                turnState.User.SetValue("user.toolCacheKey", key);
            }
            return key;
        }

        private static bool IsLocalMcpMode() =>
            string.Equals(Environment.GetEnvironmentVariable("TOOLS_MODE"), "MockMCPServer",
                StringComparison.OrdinalIgnoreCase);

        private async Task<List<AITool>> LoadLocalMcpToolsAsync(ITurnContext context)
        {
            var tools = new List<AITool>();
            var manifestPath = Path.Combine(AppContext.BaseDirectory, "ToolingManifest.json");
            if (!File.Exists(manifestPath))
            {
                _logger?.LogWarning("ToolingManifest.json not found at {Path}", manifestPath);
                return tools;
            }
            var json = await File.ReadAllTextAsync(manifestPath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers)) return tools;

            foreach (var server in servers.EnumerateArray())
            {
                if (!server.TryGetProperty("url", out var urlProp)) continue;
                var url = urlProp.GetString();
                if (string.IsNullOrEmpty(url)) continue;
                var name = server.TryGetProperty("mcpServerName", out var np) ? np.GetString() ?? "unknown" : "unknown";
                var cfg = new MCPServerConfig
                {
                    mcpServerName = name, url = url, id = name,
                    scope = string.Empty, audience = string.Empty, publisher = string.Empty
                };
                _logger?.LogInformation("Loading MCP tools from: {Name} @ {Url}", name, url);
                var mcpTools = await _toolServerConfigService!.GetMcpClientToolsAsync(context, cfg, string.Empty);
                if (mcpTools != null) tools.AddRange(mcpTools);
            }
            return tools;
        }
    }
}
