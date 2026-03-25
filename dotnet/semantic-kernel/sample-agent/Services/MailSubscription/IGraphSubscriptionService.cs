// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Agent365SemanticKernelSampleAgent.Services.MailSubscription;

/// <summary>
/// Manages Microsoft Graph mail subscriptions for agent notification delivery.
/// </summary>
public interface IGraphSubscriptionService
{
    /// <summary>
    /// Creates a Graph mail subscription using the provided Graph access token and stores the
    /// conversation reference for later proactive notification routing.
    /// </summary>
    /// <param name="graphAccessToken">A valid Graph access token for the user.</param>
    /// <param name="conversationReference">The conversation reference to use when routing notifications.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new subscription ID, or null if creation failed.</returns>
    Task<string?> CreateSubscriptionAsync(
        string graphAccessToken,
        ConversationReference conversationReference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a Graph mail subscription by acquiring a client-credentials token automatically
    /// using the ServiceConnection credentials from configuration.
    /// Requires <c>GraphSubscription:UserMailbox</c> to be set and the app to have
    /// <c>Mail.Read</c> application permission with admin consent.
    /// Uses the last stored conversation reference (see <see cref="StoreConversationReference"/>).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new subscription ID, or null if creation failed.</returns>
    Task<string?> CreateSubscriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a conversation reference so proactive turns can be initiated when a Graph
    /// notification arrives. Also used by <see cref="CreateSubscriptionAsync(CancellationToken)"/>
    /// as the routing target.
    /// </summary>
    void StoreConversationReference(ConversationReference conversationReference);

    /// <summary>
    /// Returns the last conversation reference stored via <see cref="StoreConversationReference"/>.
    /// </summary>
    ConversationReference? GetLatestConversationReference();

    /// <summary>
    /// Deletes a Graph subscription and removes it from the in-memory store.
    /// </summary>
    Task DeleteSubscriptionAsync(
        string graphAccessToken,
        string subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the expiration of an existing subscription by PATCHing its expirationDateTime.
    /// Acquires a fresh client-credentials token automatically.
    /// </summary>
    Task RenewSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the expiration of an existing subscription by PATCHing its expirationDateTime.
    /// </summary>
    Task RenewSubscriptionAsync(
        string graphAccessToken,
        string subscriptionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all tracked subscription records. Used by the background renewal service.
    /// </summary>
    IReadOnlyCollection<GraphSubscriptionRecord> GetAllSubscriptions();

    /// <summary>
    /// Looks up the <see cref="ConversationReference"/> stored for a given subscription.
    /// </summary>
    /// <param name="subscriptionId">The Graph subscription ID.</param>
    /// <param name="reference">The stored conversation reference, or null if not found.</param>
    /// <returns>True if a record was found.</returns>
    bool TryGetConversationReference(string subscriptionId, out ConversationReference? reference);

    /// <summary>
    /// Removes a subscription record from the in-memory store without calling Graph.
    /// </summary>
    void RemoveSubscription(string subscriptionId);

    /// <summary>
    /// Queries Graph for all existing subscriptions for this app and re-registers them
    /// in the in-memory store using the latest stored conversation reference.
    /// Use this to recover after an agent restart without needing to create new subscriptions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of subscriptions recovered.</returns>
    Task<int> RecoverSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches email metadata (subject, from address/name, body preview) from Graph
    /// for the given message ID. Uses a client-credentials token automatically.
    /// Returns null if the fetch fails or the email is not found.
    /// </summary>
    /// <param name="emailId">The Graph message ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EmailMetadata?> FetchEmailMetadataAsync(
        string emailId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lightweight email metadata returned by <see cref="IGraphSubscriptionService.FetchEmailMetadataAsync"/>.
/// Used to enrich <see cref="Agent365SemanticKernelSampleAgent.Services.TriggerEvaluation.Models.EmailEventData"/>
/// before trigger condition evaluation.
/// </summary>
public sealed record EmailMetadata(
    string Subject,
    string FromEmail,
    string FromName,
    string BodyPreview);
