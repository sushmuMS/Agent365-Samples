// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Agent365SemanticKernelSampleAgent.Services.MailSubscription;

/// <summary>
/// Background service that proactively renews Microsoft Graph mail subscriptions before they expire.
/// Graph caps mail subscriptions at 4230 minutes (~70.5 hours); this service renews them
/// when they are within <see cref="RenewalThreshold"/> of expiry.
/// </summary>
public sealed class SubscriptionRenewalService : BackgroundService
{
    /// <summary>How often to check for expiring subscriptions.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    /// <summary>Renew subscriptions that expire within this window.</summary>
    private static readonly TimeSpan RenewalThreshold = TimeSpan.FromHours(24);

    private readonly IGraphSubscriptionService _subscriptionService;
    private readonly ILogger<SubscriptionRenewalService> _logger;

    public SubscriptionRenewalService(
        IGraphSubscriptionService subscriptionService,
        ILogger<SubscriptionRenewalService> logger)
    {
        _subscriptionService = subscriptionService ?? throw new ArgumentNullException(nameof(subscriptionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Graph subscription renewal service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);

            if (!stoppingToken.IsCancellationRequested)
            {
                await RenewExpiringSoonAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Graph subscription renewal service stopped.");
    }

    private async Task RenewExpiringSoonAsync(CancellationToken cancellationToken)
    {
        var subscriptions = _subscriptionService.GetAllSubscriptions();

        foreach (var record in subscriptions)
        {
            var timeUntilExpiry = record.ExpirationDateTime - DateTimeOffset.UtcNow;

            if (timeUntilExpiry > RenewalThreshold)
                continue;

            _logger.LogInformation(
                "Subscription {SubscriptionId} expires in {Minutes} minutes; attempting renewal.",
                record.SubscriptionId, (int)timeUntilExpiry.TotalMinutes);

            // Use the no-token overload — it acquires a fresh client-credentials token automatically.
            await _subscriptionService.RenewSubscriptionAsync(
                record.SubscriptionId,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
