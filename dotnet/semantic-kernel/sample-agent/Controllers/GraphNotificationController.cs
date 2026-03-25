// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent365SemanticKernelSampleAgent.Agents;
using Agent365SemanticKernelSampleAgent.Services.MailSubscription;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Agent365SemanticKernelSampleAgent.Controllers;

/// <summary>
/// Receives Microsoft Graph change notifications for mailbox subscriptions and routes
/// them through the agent's existing email notification processing pipeline.
/// </summary>
/// <remarks>
/// Graph sends two types of requests to this endpoint:
/// 1. GET with ?validationToken= during subscription creation — must echo the token as text/plain.
/// 2. POST with a JSON payload when a subscribed resource changes (new email received).
///
/// Both routes are unauthenticated from Graph's perspective; security is provided by validating
/// the <c>clientState</c> secret stored in the subscription record.
/// </remarks>
[ApiController]
[Route("api/graph")]
public class GraphNotificationController : ControllerBase
{
    private readonly IGraphSubscriptionService _subscriptionService;
    private readonly IChannelAdapter _adapter;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<GraphNotificationController> _logger;

    // Thread-safe cache of recently processed Graph message IDs.
    // Suppresses duplicate notifications from multiple active subscriptions or Graph at-least-once retries.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _processedMessageIds = new();
    private static readonly TimeSpan _deduplicationWindow = TimeSpan.FromMinutes(5);

    public GraphNotificationController(
        IGraphSubscriptionService subscriptionService,
        IChannelAdapter adapter,
        IConfiguration configuration,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<GraphNotificationController> logger)
    {
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Marks a message ID as in-flight for the deduplication window.
    /// Returns <c>true</c> if this is the first time the message has been seen; <c>false</c> if it is a duplicate.
    /// </summary>
    private static bool TryMarkAsProcessed(string messageId)
    {
        var now = DateTime.UtcNow;

        // Evict expired entries to prevent unbounded growth.
        foreach (var key in _processedMessageIds.Keys.ToList())
        {
            if (_processedMessageIds.TryGetValue(key, out var ts) && (now - ts) > _deduplicationWindow)
                _processedMessageIds.TryRemove(key, out _);
        }

        // TryAdd returns false when the key already exists → this is a duplicate.
        return _processedMessageIds.TryAdd(messageId, now);
    }

    /// <summary>
    /// Development-only endpoint: creates a Graph mail subscription using the client-credentials
    /// token acquired from configuration. Call this after sending at least one message from the
    /// Agents Playground (so a conversation reference is stored).
    /// </summary>
    /// <remarks>
    /// GET /api/graph/subscribe
    /// Returns the new subscription ID on success.
    /// This endpoint is unauthenticated; restrict or remove it before deploying to production.
    /// </remarks>
    [HttpGet("subscribe")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateSubscriptionAsync(CancellationToken cancellationToken)
    {
        var convRef = _subscriptionService.GetLatestConversationReference();
        if (convRef == null)
        {
            return BadRequest(new
            {
                error = "No conversation reference found. Send at least one message from Agents Playground first, then retry."
            });
        }

        var subscriptionId = await _subscriptionService.CreateSubscriptionAsync(cancellationToken);

        if (subscriptionId == null)
        {
            return StatusCode(500, new
            {
                error = "Subscription creation failed. Check agent logs for details. " +
                        "Ensure Connections:ServiceConnection:Settings:ClientSecret is set (dotnet user-secrets) " +
                        "and the app has Mail.Read application permission with admin consent."
            });
        }

        return Ok(new { subscriptionId, message = "Graph mail subscription created successfully." });
    }

    /// <summary>
    /// Re-imports existing Graph subscriptions from Microsoft Graph into the in-memory store.
    /// Use this after an agent restart to recover without creating new subscriptions.
    /// Requires at least one prior message from Agents Playground to have a conversation reference.
    /// GET /api/graph/recover
    /// </summary>
    [HttpGet("recover")]
    [AllowAnonymous]
    public async Task<IActionResult> RecoverSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var convRef = _subscriptionService.GetLatestConversationReference();
        if (convRef == null)
        {
            return BadRequest(new
            {
                error = "No conversation reference found. Send at least one message from Agents Playground first, then retry."
            });
        }

        var recovered = await _subscriptionService.RecoverSubscriptionsAsync(cancellationToken);
        if (recovered == 0)
        {
            return Ok(new { recovered = 0, message = "No matching subscriptions found in Graph, or token acquisition failed. Check agent logs." });
        }

        return Ok(new { recovered, message = $"Successfully recovered {recovered} subscription(s). Email notifications are now active." });
    }

    /// <summary>
    /// Keeps only the newest Graph subscription and deletes the rest.
    /// GET /api/graph/cleanup
    /// </summary>
    [HttpGet("cleanup")]
    [AllowAnonymous]
    public async Task<IActionResult> CleanupSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var all = _subscriptionService.GetAllSubscriptions()
            .OrderByDescending(s => s.ExpirationDateTime)
            .ToList();

        if (all.Count == 0)
            return Ok(new { message = "No subscriptions in memory. Call /api/graph/recover first." });

        if (all.Count == 1)
            return Ok(new { kept = all[0].SubscriptionId, deleted = 0, message = "Only one subscription exists, nothing to clean up." });

        var keep = all[0];
        var toDelete = all.Skip(1).ToList();

        foreach (var record in toDelete)
        {
            await _subscriptionService.DeleteSubscriptionAsync(string.Empty, record.SubscriptionId, cancellationToken);
            _logger.LogInformation("Deleted duplicate subscription {SubscriptionId}.", record.SubscriptionId);
        }

        return Ok(new
        {
            kept = keep.SubscriptionId,
            deleted = toDelete.Count,
            message = $"Kept subscription {keep.SubscriptionId}, deleted {toDelete.Count} duplicate(s)."
        });
    }

    /// <summary>
    /// Handles the Graph subscription validation handshake.
    /// Graph sends a GET with a validationToken query parameter; we must echo it as text/plain
    /// within 10 seconds for the subscription to be accepted.
    /// </summary>
    [HttpGet("notifications")]
    [AllowAnonymous]
    public IActionResult ValidateWebhook([FromQuery] string validationToken)
    {
        if (string.IsNullOrEmpty(validationToken))
        {
            _logger.LogWarning("Graph webhook validation request received without validationToken.");
            return BadRequest("Missing validationToken");
        }

        _logger.LogDebug("Graph webhook validation request received; echoing token.");
        return Content(validationToken, "text/plain");
    }

    /// <summary>
    /// Receives Microsoft Graph POST requests at the notification URL.
    /// Graph sends two types of POST requests:
    /// 1. Validation handshake (POST with ?validationToken= in query string, empty body) — must echo token as text/plain.
    /// 2. Change notifications (POST with JSON body containing new email events).
    /// </summary>
    /// <remarks>
    /// Graph expects an HTTP 202 Accepted response for notifications within a few seconds.
    /// All notification processing is queued as fire-and-forget background tasks.
    /// </remarks>
    [HttpPost("notifications")]
    [AllowAnonymous]
    public async Task<IActionResult> ReceiveNotification(CancellationToken cancellationToken)
    {
        // Graph validation handshake: POST with validationToken query parameter, empty body.
        // Must respond 200 OK with the token as text/plain within 10 seconds.
        var validationToken = Request.Query["validationToken"].FirstOrDefault();
        if (!string.IsNullOrEmpty(validationToken))
        {
            _logger.LogInformation("Graph webhook validation (POST) received; echoing token.");
            return Content(validationToken, "text/plain");
        }

        // Change notification: read and parse the JSON body.
        JsonNode? body = null;
        try
        {
            body = await JsonNode.ParseAsync(Request.Body, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Graph notification body.");
            return BadRequest();
        }

        var notifications = body?["value"]?.AsArray();
        if (notifications == null)
            return Accepted();

        // Process each notification in a fresh DI scope so scoped services (IAgent, Kernel)
        // are not affected by the request scope being disposed when we return 202.
        foreach (var notification in notifications)
        {
            var notifCopy = notification;
            _ = Task.Run(
                () => ProcessNotificationInScopeAsync(notifCopy, CancellationToken.None),
                CancellationToken.None);
        }

        return Accepted();
    }

    private async Task ProcessNotificationInScopeAsync(
        JsonNode? notification,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<IAgent>();
        await ProcessNotificationAsync(notification, agent, cancellationToken);
    }

    private async Task ProcessNotificationAsync(
        JsonNode? notification,
        IAgent agent,
        CancellationToken cancellationToken)
    {
        var subscriptionId = notification?["subscriptionId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            _logger.LogWarning("Graph notification missing subscriptionId; skipping.");
            return;
        }

        if (!_subscriptionService.TryGetConversationReference(subscriptionId, out var convRef) ||
            convRef == null)
        {
            _logger.LogWarning(
                "No conversation reference found for subscription {SubscriptionId}; skipping.",
                subscriptionId);
            return;
        }

        // Extract the Graph message ID from the notification payload.
        // The full email content is retrieved later via mcp_MailTools using this ID.
        var messageId = notification?["resourceData"]?["id"]?.GetValue<string>()
            ?? ExtractMessageIdFromResource(notification?["resource"]?.GetValue<string>());

        if (string.IsNullOrEmpty(messageId))
        {
            _logger.LogWarning(
                "Could not extract message ID from Graph notification for subscription {SubscriptionId}.",
                subscriptionId);
            return;
        }

        // Deduplicate: if multiple subscriptions are active or Graph retries the same notification,
        // only the first arrival within the deduplication window is processed.
        if (!TryMarkAsProcessed(messageId))
        {
            _logger.LogInformation(
                "Skipping duplicate Graph notification for message {MessageId} (already processing).",
                messageId);
            return;
        }

        _logger.LogInformation(
            "Routing Graph email notification for subscription {SubscriptionId}, message {MessageId}.",
            subscriptionId, messageId);

        // Build an AgentNotificationActivity directly by setting its properties.
        // The From.Name is set to a placeholder here; HandleEmailNotificationWithTriggersAsync
        // retrieves the full email content (including sender details) via mcp_MailTools.
        var notificationActivity = new AgentNotificationActivity(new Activity())
        {
            NotificationType = NotificationTypeEnum.EmailNotification,
            From = new ChannelAccount
            {
                Id = string.Empty,
                Name = "New Email"
            },
            EmailNotification = new EmailReference
            {
                Id = messageId,
                ConversationId = notification?["resourceData"]?["conversationId"]?.GetValue<string>()
                    ?? string.Empty
            }
        };

        // Use AgentClaims.CreateIdentity so the adapter recognises this as an authenticated turn.
        var appId = _configuration["Connections:ServiceConnection:Settings:ClientId"] ?? string.Empty;
        // anonymous: false means the caller IS authenticated; appId is the sender identity.
        var identity = AgentClaims.CreateIdentity(appId, anonymous: false, appId: appId);

        try
        {
            await _adapter.ContinueConversationAsync(
                identity,
                convRef,
                async (turnContext, ct) =>
                {
                    if (agent is MyAgent myAgent)
                    {
                        await myAgent.RouteGraphEmailNotificationAsync(
                            turnContext,
                            notificationActivity,
                            ct);
                    }
                    else
                    {
                        _logger.LogError(
                            "Agent is not of type MyAgent; cannot route Graph email notification.");
                    }
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error routing Graph notification for subscription {SubscriptionId}.",
                subscriptionId);
        }
    }

    /// <summary>
    /// Attempts to parse the message ID from a Graph resource path such as
    /// "Users/user-id/Messages/message-id" when it is not present in resourceData.
    /// </summary>
    private static string? ExtractMessageIdFromResource(string? resource)
    {
        if (string.IsNullOrEmpty(resource))
            return null;

        // Resource paths look like: Users/{id}/Messages/{messageId}
        var lastSlash = resource.LastIndexOf('/');
        return lastSlash >= 0 ? resource[(lastSlash + 1)..] : null;
    }
}
