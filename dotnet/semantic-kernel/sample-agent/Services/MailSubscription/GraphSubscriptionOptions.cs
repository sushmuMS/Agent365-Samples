// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Agent365SemanticKernelSampleAgent.Services.MailSubscription;

/// <summary>
/// Configuration options for Microsoft Graph mail subscriptions.
/// Bind from the "GraphSubscription" section of appsettings.json.
/// </summary>
public sealed class GraphSubscriptionOptions
{
    public const string SectionName = "GraphSubscription";

    /// <summary>
    /// The publicly reachable HTTPS URL that Microsoft Graph will POST change notifications to.
    /// For local development use a dev tunnel (https://aka.ms/devtunnels) or ngrok.
    /// </summary>
    public string NotificationUrl { get; set; } = string.Empty;

    /// <summary>
    /// The change type to subscribe to. Defaults to "created" (new emails only).
    /// </summary>
    public string ChangeType { get; set; } = "created";

    /// <summary>
    /// The Graph resource to watch.
    /// When <see cref="UserMailbox"/> is set, this is overridden to "users/{UserMailbox}/messages"
    /// so that an application (client-credentials) token can be used.
    /// Defaults to "me/messages" for delegated-auth scenarios.
    /// </summary>
    public string Resource { get; set; } = "me/messages";

    /// <summary>
    /// The mailbox to monitor when using application permissions (client credentials flow).
    /// Set to the UPN or OID of the user, e.g. "user@contoso.com".
    /// When set, the subscription resource becomes "users/{UserMailbox}/messages" and a
    /// client-credentials token is acquired automatically using the ServiceConnection credentials.
    /// Leave empty to use delegated auth with the "me/messages" resource.
    /// </summary>
    public string UserMailbox { get; set; } = string.Empty;

    /// <summary>
    /// How many hours ahead to set the subscription expiration.
    /// Graph caps mail subscriptions at 4230 minutes (~70.5 hours); 71 hours provides a safe margin.
    /// </summary>
    public int ExpirationHours { get; set; } = 71;
}
