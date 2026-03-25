// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent365SemanticKernelSampleAgent.Services.MailSubscription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent365SemanticKernelSampleAgent.Extensions;

/// <summary>
/// Extension methods to register Microsoft Graph mail subscription services.
/// </summary>
public static class MailSubscriptionExtensions
{
    /// <summary>
    /// Registers <see cref="IGraphSubscriptionService"/> and the background
    /// <see cref="SubscriptionRenewalService"/> with the DI container.
    /// </summary>
    public static IServiceCollection AddMailSubscription(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GraphSubscriptionOptions>(
            configuration.GetSection(GraphSubscriptionOptions.SectionName));

        services.AddSingleton<IGraphSubscriptionService, GraphSubscriptionService>();
        services.AddHostedService<SubscriptionRenewalService>();

        return services;
    }
}
