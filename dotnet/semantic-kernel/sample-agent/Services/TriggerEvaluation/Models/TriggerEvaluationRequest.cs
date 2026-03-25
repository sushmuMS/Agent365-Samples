// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Agent365SemanticKernelSampleAgent.Services.TriggerEvaluation.Models;

/// <summary>
/// Request model for trigger evaluation API.
/// </summary>
public sealed class TriggerEvaluationRequest
{
    /// <summary>
    /// Gets or sets the agent ID for filtering triggers.
    /// </summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; set; }

    /// <summary>
    /// Gets or sets the event type (email, document, message).
    /// </summary>
    [JsonPropertyName("eventType")]
    public required string EventType { get; set; }

    /// <summary>
    /// Gets or sets the event data to evaluate against trigger conditions.
    /// </summary>
    [JsonPropertyName("eventData")]
    public required object EventData { get; set; }
}

/// <summary>
/// Represents a skill invocation from evaluate_event_triggers_v2 skillsToExecute[].
/// </summary>
public sealed class SkillToExecute
{
    [JsonPropertyName("skillId")]
    public string SkillId { get; init; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("parameters")]
    public System.Text.Json.JsonElement? Parameters { get; init; }

    [JsonPropertyName("onSuccess")]
    public string? OnSuccess { get; init; }

    [JsonPropertyName("onFailure")]
    public string? OnFailure { get; init; }

    /// <summary>Pre-built query synthesized by SkillQueryBuilder on the MCP Platform server.
    /// Pass directly to mcp_WorkIQSandbox.execute_skill as the query parameter.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; init; }
}

/// <summary>
/// Response model from evaluate_event_triggers_v2.
/// Maps both V1 (promptInstructions) and V2 (skillsToExecute) fields.
/// </summary>
public sealed class TriggerEvaluationResponse
{
    /// <summary>
    /// Gets an empty response indicating no triggers matched.
    /// </summary>
    public static TriggerEvaluationResponse Empty => new()
    {
        IsActive = false,
        MatchedTriggerCount = 0,
        Instructions = [],
        PromptInstructions = [],
        SkillsToExecute = null
    };

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    [JsonPropertyName("matchedTriggerCount")]
    public int MatchedTriggerCount { get; init; }

    /// <summary>V1 compat — free-text instructions from matched triggers.</summary>
    [JsonPropertyName("promptInstructions")]
    public string[] PromptInstructions { get; init; } = [];

    /// <summary>Legacy alias — LLM may reformat promptInstructions as "instructions".</summary>
    [JsonPropertyName("instructions")]
    public string[] Instructions { get; init; } = [];

    /// <summary>V2 — typed skill plan to execute via mcp_SkillsExecutorServer.</summary>
    [JsonPropertyName("skillsToExecute")]
    public SkillToExecute[]? SkillsToExecute { get; init; }

    [JsonIgnore]
    public bool HasInstructions =>
        (PromptInstructions?.Length > 0) || (Instructions?.Length > 0);

    [JsonIgnore]
    public bool HasSkillsToExecute =>
        SkillsToExecute?.Length > 0;

    public string GetCombinedInstructions(string separator = "\n\n")
    {
        var all = new System.Collections.Generic.List<string>();
        if (PromptInstructions?.Length > 0) all.AddRange(PromptInstructions);
        if (Instructions?.Length > 0) all.AddRange(Instructions);
        return all.Count > 0 ? string.Join(separator, all) : string.Empty;
    }
}
