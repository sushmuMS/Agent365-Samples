// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Agents.Core.Models;
using System;

namespace Agent365SemanticKernelSampleAgent.Services.MailSubscription;

/// <summary>
/// Holds the per-user state needed to manage a Microsoft Graph mail subscription lifecycle.
/// </summary>
/// <remarks>
/// TODO: In production, persist these records in <see cref="Microsoft.Agents.Storage.IStorage"/>
/// or a database so they survive agent restarts.
/// </remarks>
public sealed class GraphSubscriptionRecord
{
    /// <summary>The Graph subscription ID returned by POST /subscriptions.</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>When Graph will stop delivering notifications for this subscription.</summary>
    public DateTimeOffset ExpirationDateTime { get; set; }

    /// <summary>
    /// Conversation reference captured at install time; used for proactive notification routing.
    /// </summary>
    public ConversationReference ConversationReference { get; set; } = new();

    /// <summary>
    /// The Graph access token used to create the subscription.
    /// Stored so the background renewal service can refresh the subscription.
    /// </summary>
    /// <remarks>
    /// TODO: In production, store a refresh token or use a managed identity instead of
    /// caching the access token. Access tokens expire (typically after 1 hour).
    /// </remarks>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>When <see cref="AccessToken"/> expires.</summary>
    public DateTimeOffset AccessTokenExpiry { get; set; }
}
