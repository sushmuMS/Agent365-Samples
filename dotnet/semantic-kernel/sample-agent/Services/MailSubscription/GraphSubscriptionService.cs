// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Agent365SemanticKernelSampleAgent.Services.MailSubscription;

/// <summary>
/// Creates and manages Microsoft Graph mail subscriptions using the Graph REST API.
/// Subscription records are stored in an in-memory dictionary keyed by subscription ID.
/// </summary>
/// <remarks>
/// TODO: In production, replace the static in-memory dictionary with a persistent store
/// (e.g., <see cref="Microsoft.Agents.Storage.IStorage"/>) so subscriptions survive restarts.
/// </remarks>
public sealed class GraphSubscriptionService : IGraphSubscriptionService
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GraphSubscriptionOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GraphSubscriptionService> _logger;

    // Static so the singleton service retains state across DI scope lifetimes.
    // TODO: Replace with IStorage-backed persistence for production.
    private static readonly ConcurrentDictionary<string, GraphSubscriptionRecord> _subscriptions = new();

    // Stores the most recent conversation reference so CreateSubscriptionAsync() can use it.
    private static ConversationReference? _latestConversationReference;

    public GraphSubscriptionService(
        IHttpClientFactory httpClientFactory,
        IOptions<GraphSubscriptionOptions> options,
        IConfiguration configuration,
        ILogger<GraphSubscriptionService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public void StoreConversationReference(ConversationReference conversationReference)
        => _latestConversationReference = conversationReference;

    /// <inheritdoc/>
    public ConversationReference? GetLatestConversationReference()
        => _latestConversationReference;

    /// <inheritdoc/>
    public async Task<string?> CreateSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        var convRef = _latestConversationReference;
        if (convRef == null)
        {
            _logger.LogWarning(
                "Cannot create Graph subscription: no conversation reference stored. " +
                "Send at least one message from the Agents Playground first.");
            return null;
        }

        var token = await AcquireClientCredentialsTokenAsync(cancellationToken);
        if (token == null)
            return null;

        return await CreateSubscriptionCoreAsync(token, convRef, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string?> CreateSubscriptionAsync(
        string graphAccessToken,
        ConversationReference conversationReference,
        CancellationToken cancellationToken = default)
    {
        // If caller passes an empty token, fall back to client credentials.
        if (string.IsNullOrEmpty(graphAccessToken))
        {
            _logger.LogInformation(
                "No external Graph token provided; attempting client-credentials token acquisition.");
            var ccToken = await AcquireClientCredentialsTokenAsync(cancellationToken);
            if (ccToken == null)
                return null;
            graphAccessToken = ccToken;
        }

        // Store the conversation reference so the no-arg overload can also use it.
        _latestConversationReference = conversationReference;
        return await CreateSubscriptionCoreAsync(graphAccessToken, conversationReference, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteSubscriptionAsync(
        string graphAccessToken,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(subscriptionId))
            return;

        // Fall back to client credentials if no token provided.
        if (string.IsNullOrEmpty(graphAccessToken))
        {
            var ccToken = await AcquireClientCredentialsTokenAsync(cancellationToken);
            graphAccessToken = ccToken ?? string.Empty;
        }

        if (string.IsNullOrEmpty(graphAccessToken))
        {
            _logger.LogWarning("Cannot delete subscription {SubscriptionId}: no access token.", subscriptionId);
            _subscriptions.TryRemove(subscriptionId, out _);
            return;
        }

        using var client = CreateGraphClient(graphAccessToken);
        try
        {
            var response = await client.DeleteAsync(
                $"{GraphBaseUrl}/subscriptions/{subscriptionId}",
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Deleted Graph subscription {SubscriptionId}.", subscriptionId);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to delete Graph subscription {SubscriptionId}: HTTP {Status}.",
                    subscriptionId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Graph subscription {SubscriptionId}.", subscriptionId);
        }
        finally
        {
            _subscriptions.TryRemove(subscriptionId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task RenewSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var token = await AcquireClientCredentialsTokenAsync(cancellationToken);
        if (token == null)
        {
            _logger.LogWarning(
                "Cannot renew subscription {SubscriptionId}: failed to acquire client-credentials token.",
                subscriptionId);
            return;
        }

        await RenewSubscriptionAsync(token, subscriptionId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RenewSubscriptionAsync(
        string graphAccessToken,
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        var expiration = DateTimeOffset.UtcNow.AddHours(_options.ExpirationHours);
        var body = JsonSerializer.Serialize(new { expirationDateTime = expiration.ToString("o") });

        using var client = CreateGraphClient(graphAccessToken);
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{GraphBaseUrl}/subscriptions/{subscriptionId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (_subscriptions.TryGetValue(subscriptionId, out var record))
                {
                    record.ExpirationDateTime = expiration;
                }
                _logger.LogInformation(
                    "Renewed Graph subscription {SubscriptionId} until {Expiry}.",
                    subscriptionId, expiration);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to renew Graph subscription {SubscriptionId}: HTTP {Status}.",
                    subscriptionId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing Graph subscription {SubscriptionId}.", subscriptionId);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<GraphSubscriptionRecord> GetAllSubscriptions()
        => _subscriptions.Values.ToList();

    /// <inheritdoc/>
    public bool TryGetConversationReference(string subscriptionId, out ConversationReference? reference)
    {
        if (_subscriptions.TryGetValue(subscriptionId, out var record))
        {
            reference = record.ConversationReference;
            return true;
        }

        reference = null;
        return false;
    }

    /// <inheritdoc/>
    public void RemoveSubscription(string subscriptionId)
        => _subscriptions.TryRemove(subscriptionId, out _);

    /// <inheritdoc/>
    public async Task<EmailMetadata?> FetchEmailMetadataAsync(
        string emailId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(emailId))
            return null;

        var token = await AcquireClientCredentialsTokenAsync(cancellationToken);
        if (token == null)
        {
            _logger.LogWarning("Cannot fetch email metadata: failed to acquire token.");
            return null;
        }

        var mailbox = _options.UserMailbox;
        if (string.IsNullOrEmpty(mailbox))
        {
            _logger.LogWarning("Cannot fetch email metadata: GraphSubscription:UserMailbox is not configured.");
            return null;
        }

        var url = $"{GraphBaseUrl}/users/{mailbox}/messages/{emailId}?$select=subject,from,bodyPreview";
        using var client = CreateGraphClient(token);

        try
        {
            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to fetch email metadata for message {EmailId}: HTTP {Status}.",
                    emailId, (int)response.StatusCode);
                return null;
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var subject = json?["subject"]?.GetValue<string>() ?? string.Empty;
            var fromEmail = json?["from"]?["emailAddress"]?["address"]?.GetValue<string>() ?? string.Empty;
            var fromName = json?["from"]?["emailAddress"]?["name"]?.GetValue<string>() ?? string.Empty;
            var bodyPreview = json?["bodyPreview"]?.GetValue<string>() ?? string.Empty;

            _logger.LogDebug(
                "Fetched email metadata: Subject='{Subject}', From='{FromName}' <{FromEmail}>",
                subject, fromName, fromEmail);

            return new EmailMetadata(subject, fromEmail, fromName, bodyPreview);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching email metadata for message {EmailId}.", emailId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<int> RecoverSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var convRef = _latestConversationReference;
        if (convRef == null)
        {
            _logger.LogWarning("Cannot recover subscriptions: no conversation reference stored. Send a message from Agents Playground first.");
            return 0;
        }

        var token = await AcquireClientCredentialsTokenAsync(cancellationToken);
        if (token == null)
            return 0;

        using var client = CreateGraphClient(token);
        JsonNode? json;
        try
        {
            var response = await client.GetAsync($"{GraphBaseUrl}/subscriptions", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to list Graph subscriptions: HTTP {Status}. Body: {Error}", (int)response.StatusCode, error);
                return 0;
            }
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Graph subscriptions.");
            return 0;
        }

        var subscriptions = json?["value"]?.AsArray();
        if (subscriptions == null)
            return 0;

        int recovered = 0;
        foreach (var sub in subscriptions)
        {
            var subscriptionId = sub?["id"]?.GetValue<string>();
            var notificationUrl = sub?["notificationUrl"]?.GetValue<string>();
            var expiryStr = sub?["expirationDateTime"]?.GetValue<string>();

            if (string.IsNullOrEmpty(subscriptionId))
                continue;

            // Only recover subscriptions pointing at our notification URL
            if (!string.IsNullOrEmpty(_options.NotificationUrl) &&
                !string.IsNullOrEmpty(notificationUrl) &&
                !notificationUrl.Equals(_options.NotificationUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expiry = DateTimeOffset.TryParse(expiryStr, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddHours(1);
            _subscriptions[subscriptionId] = new GraphSubscriptionRecord
            {
                SubscriptionId = subscriptionId,
                ExpirationDateTime = expiry,
                ConversationReference = convRef,
                AccessToken = token,
                AccessTokenExpiry = DateTimeOffset.UtcNow.AddMinutes(55)
            };

            _logger.LogInformation("Recovered Graph subscription {SubscriptionId} expiring {Expiry}.", subscriptionId, expiry);
            recovered++;
        }

        return recovered;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<string?> CreateSubscriptionCoreAsync(
        string graphAccessToken,
        ConversationReference conversationReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.NotificationUrl) ||
            _options.NotificationUrl.StartsWith("<<", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "GraphSubscription:NotificationUrl is not configured. " +
                "Set it to a publicly reachable HTTPS URL before installing the agent.");
            return null;
        }

        // Use users/{mailbox}/messages when UserMailbox is configured (app permissions).
        // Fall back to the raw Resource setting otherwise.
        // Remove any previously registered subscriptions before creating a new one.
        // This prevents multiple active subscriptions from firing duplicate notifications for every email.
        var existing = GetAllSubscriptions().ToList();
        foreach (var record in existing)
        {
            _logger.LogInformation(
                "Removing existing subscription {SubscriptionId} before creating a new one.",
                record.SubscriptionId);
            await DeleteSubscriptionAsync(graphAccessToken, record.SubscriptionId, cancellationToken);
        }

        var resource = !string.IsNullOrEmpty(_options.UserMailbox)
            ? $"users/{_options.UserMailbox}/messages"
            : _options.Resource;

        var expiration = DateTimeOffset.UtcNow.AddHours(_options.ExpirationHours);

        var body = JsonSerializer.Serialize(new
        {
            changeType = _options.ChangeType,
            notificationUrl = _options.NotificationUrl,
            resource,
            expirationDateTime = expiration.ToString("o"),
            clientState = Guid.NewGuid().ToString("N")
        });

        using var client = CreateGraphClient(graphAccessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(
                $"{GraphBaseUrl}/subscriptions",
                new StringContent(body, Encoding.UTF8, "application/json"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP error creating Graph subscription.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Graph subscription creation failed: HTTP {Status}. Body: {Error}",
                (int)response.StatusCode, error);
            return null;
        }

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var subscriptionId = json?["id"]?.GetValue<string>();

        if (string.IsNullOrEmpty(subscriptionId))
        {
            _logger.LogError("Graph subscription response did not contain an id.");
            return null;
        }

        _subscriptions[subscriptionId] = new GraphSubscriptionRecord
        {
            SubscriptionId = subscriptionId,
            ExpirationDateTime = expiration,
            ConversationReference = conversationReference,
            // Token stored for legacy reference; renewal now uses client credentials.
            AccessToken = graphAccessToken,
            AccessTokenExpiry = DateTimeOffset.UtcNow.AddMinutes(55)
        };

        _logger.LogInformation(
            "Created Graph mail subscription {SubscriptionId} expiring {Expiry} for resource {Resource}.",
            subscriptionId, expiration, resource);

        return subscriptionId;
    }

    /// <summary>
    /// Acquires an application access token via client-credentials flow using the
    /// ServiceConnection settings from configuration.
    /// </summary>
    private async Task<string?> AcquireClientCredentialsTokenAsync(CancellationToken cancellationToken)
    {
        var tenantId = _configuration["TokenValidation:TenantId"]
            ?? ExtractTenantIdFromAuthority(_configuration["Connections:ServiceConnection:Settings:AuthorityEndpoint"]);
        var clientId = _configuration["Connections:ServiceConnection:Settings:ClientId"];
        var clientSecret = _configuration["Connections:ServiceConnection:Settings:ClientSecret"];

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogWarning(
                "Cannot acquire client-credentials token: missing TenantId, ClientId, or ClientSecret. " +
                "Set Connections:ServiceConnection:Settings:ClientSecret via dotnet user-secrets.");
            return null;
        }

        var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://graph.microsoft.com/.default"
        });

        using var client = _httpClientFactory.CreateClient();
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(tokenUrl, body, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP error acquiring client-credentials token.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Token endpoint returned HTTP {Status}: {Error}",
                (int)response.StatusCode, error);
            return null;
        }

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var token = json?["access_token"]?.GetValue<string>();

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogError("Token response did not contain access_token.");
            return null;
        }

        _logger.LogDebug("Acquired client-credentials token for Graph.");
        return token;
    }

    private static string? ExtractTenantIdFromAuthority(string? authorityEndpoint)
    {
        // e.g. https://login.microsoftonline.com/{tenantId}
        if (string.IsNullOrEmpty(authorityEndpoint))
            return null;

        var uri = new Uri(authorityEndpoint);
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length > 0 ? segments[^1] : null;
    }

    private HttpClient CreateGraphClient(string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}
