// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Agent365SemanticKernelSampleAgent.Services.TriggerEvaluation.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App.UserAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent365SemanticKernelSampleAgent.Services.TriggerEvaluation;

/// <summary>
/// Service interface for evaluating notifications against trigger definitions.
/// </summary>
public interface ITriggerEvaluationService
{
    /// <summary>
    /// Builds a prompt that instructs the agent to call the evaluate_event_triggers MCP tool.
    /// The agent will invoke the tool and return the evaluation results.
    /// </summary>
    /// <param name="eventData">The event data to evaluate.</param>
    /// <returns>A prompt string for the agent to execute trigger evaluation.</returns>
    string BuildTriggerEvaluationPrompt(NotificationEventData eventData);

    /// <summary>
    /// Parses the agent's response from trigger evaluation into a structured response.
    /// </summary>
    /// <param name="agentResponse">The raw response from the agent.</param>
    /// <returns>The parsed trigger evaluation response.</returns>
    TriggerEvaluationResponse ParseTriggerEvaluationResponse(string agentResponse);
}

/// <summary>
/// Service for evaluating events against trigger definitions via MCP Tool.
///
/// Design Principles:
/// - Uses the agent's existing MCP tool infrastructure
/// - The agent calls 'evaluate_event_triggers' tool on mcp_TaskPersonalizationServer
/// - No separate MCP client connection needed
///
/// Flow:
/// 1. Agent receives notification (email, document comment, etc.)
/// 2. Event data is extracted from the notification
/// 3. This service builds a prompt for the agent to call the MCP tool
/// 4. Agent invokes 'evaluate_event_triggers' via its registered MCP tools
/// 5. If conditions match, the associated MCPPrompt instructions are returned
/// 6. Agent uses these instructions to personalize its response
/// </summary>
public sealed class TriggerEvaluationService : ITriggerEvaluationService
{
    private readonly ILogger<TriggerEvaluationService> _logger;
    private readonly TriggerEvaluationOptions _options;

    private const string ToolName = "evaluate_event_triggers_v2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="TriggerEvaluationService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="options">The configuration options.</param>
    public TriggerEvaluationService(
        ILogger<TriggerEvaluationService> logger,
        IOptions<TriggerEvaluationOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public string BuildTriggerEvaluationPrompt(NotificationEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var eventDataJson = JsonSerializer.Serialize(eventData, eventData.GetType(), JsonOptions);

        _logger.LogDebug(
            "Building trigger evaluation prompt for event type {EventType}",
            eventData.EventType);

        // Build a prompt that instructs the agent to call the MCP tool
        return $@"Call the '{ToolName}' tool with the following parameters:
- eventType: ""{eventData.EventType}""
- eventDataJson: {eventDataJson}

Return ONLY a JSON object with these fields (no markdown fences, no explanation):
- isActive: boolean indicating if any triggers matched
- matchedTriggerCount: number of triggers that matched
- promptInstructions: array of instruction strings from matching triggers (from the tool's promptInstructions field)
- skillsToExecute: array of skill objects from the tool's skillsToExecute field (null if none), each with fields: skillId, order, parameters, onSuccess, onFailure, query

If the tool is not available or fails, return: {{""isActive"": false, ""matchedTriggerCount"": 0, ""promptInstructions"": [], ""skillsToExecute"": null}}";
    }

    /// <inheritdoc/>
    public TriggerEvaluationResponse ParseTriggerEvaluationResponse(string agentResponse)
    {
        if (string.IsNullOrWhiteSpace(agentResponse))
        {
            _logger.LogWarning("Empty agent response for trigger evaluation");
            return TriggerEvaluationResponse.Empty;
        }

        try
        {
            // Try to extract JSON from the response
            var jsonStart = agentResponse.IndexOf('{');
            var jsonEnd = agentResponse.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonString = agentResponse.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var response = JsonSerializer.Deserialize<TriggerEvaluationResponse>(jsonString, JsonOptions);

                if (response != null)
                {
                    LogResult(response);
                    return response;
                }
            }

            _logger.LogWarning("Could not extract JSON from agent response");
            return TriggerEvaluationResponse.Empty;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse trigger evaluation response from agent");
            return TriggerEvaluationResponse.Empty;
        }
    }

    /// <summary>
    /// Logs the evaluation result.
    /// </summary>
    private void LogResult(TriggerEvaluationResponse result)
    {
        if (result.IsActive && result.HasSkillsToExecute)
        {
            _logger.LogInformation(
                "Trigger evaluation matched (V2)! Matched {MatchedCount} triggers with {SkillCount} skills to execute",
                result.MatchedTriggerCount,
                result.SkillsToExecute!.Length);

            foreach (var skill in result.SkillsToExecute.Take(3))
            {
                _logger.LogInformation("Skill to execute: skillId={SkillId}, order={Order}", skill.SkillId, skill.Order);
            }
        }
        else if (result.IsActive && result.HasInstructions)
        {
            _logger.LogInformation(
                "Trigger evaluation matched (V1)! Matched {MatchedCount} triggers with {InstructionCount} instructions",
                result.MatchedTriggerCount,
                result.Instructions.Length);

            foreach (var instruction in result.Instructions.Take(3))
            {
                _logger.LogDebug("Instruction preview: {Instruction}",
                    instruction.Length > 100 ? instruction[..100] + "..." : instruction);
            }
        }
        else if (result.IsActive)
        {
            _logger.LogWarning(
                "Trigger matched (isActive=true, count={Count}) but no skillsToExecute and no instructions! " +
                "Raw SkillsToExecute={SkillsToExecute}",
                result.MatchedTriggerCount,
                result.SkillsToExecute == null ? "null" : $"Length={result.SkillsToExecute.Length}");
        }
        else
        {
            _logger.LogDebug("No triggers matched for this event");
        }
    }
}
