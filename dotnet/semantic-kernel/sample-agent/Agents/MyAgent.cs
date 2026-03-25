// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Agent365SemanticKernelSampleAgent.Agents;
using Agent365SemanticKernelSampleAgent.Extensions;
using Agent365SemanticKernelSampleAgent.Services.MailSubscription;
using Agent365SemanticKernelSampleAgent.Services.TriggerEvaluation;
using Agent365SemanticKernelSampleAgent.Services.TriggerEvaluation.Models;
using Agent365SemanticKernelSampleAgent.telemetry;
using AgentNotification;
using Microsoft.Agents.A365.Notifications.Models;
using Microsoft.Agents.A365.Observability.Caching;
using Microsoft.Agents.A365.Tooling.Extensions.SemanticKernel.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Agent365SemanticKernelSampleAgent.Agents;

public class MyAgent : AgentApplication
{
    private readonly Kernel _kernel;
    private readonly IMcpToolRegistrationService _toolsService;
    private readonly IExporterTokenCache<AgenticTokenStruct> _agentTokenCache;
    private readonly ILogger<MyAgent> _logger;
    private readonly IConfiguration _configuration;
    private readonly ITriggerEvaluationService _triggerEvaluationService;
    private readonly INotificationEventExtractor _eventExtractor;
    private readonly IGraphSubscriptionService _graphSubscriptionService;

    // Auth handler name constants
    private const string AgenticIdAuthHandler = "agentic";
    private const string MyAuthHandler = "me";

    // TODO: These static properties create a race condition in multi-tenant scenarios.
    // In production, store per-user/per-conversation state in ITurnState or a proper state store.
    internal static bool IsApplicationInstalled { get; set; } = false;
    internal static bool TermsAndConditionsAccepted { get; set; } = false;

    public MyAgent(
        AgentApplicationOptions options,
        IConfiguration configuration,
        Kernel kernel,
        IMcpToolRegistrationService toolService,
        IExporterTokenCache<AgenticTokenStruct> agentTokenCache,
        ILogger<MyAgent> logger,
        ITriggerEvaluationService triggerEvaluationService,
        INotificationEventExtractor eventExtractor,
        IGraphSubscriptionService graphSubscriptionService) : base(options)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _toolsService = toolService ?? throw new ArgumentNullException(nameof(toolService));
        _agentTokenCache = agentTokenCache ?? throw new ArgumentNullException(nameof(agentTokenCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _triggerEvaluationService = triggerEvaluationService ?? throw new ArgumentNullException(nameof(triggerEvaluationService));
        _eventExtractor = eventExtractor ?? throw new ArgumentNullException(nameof(eventExtractor));
        _graphSubscriptionService = graphSubscriptionService ?? throw new ArgumentNullException(nameof(graphSubscriptionService));

        // Disable for development purpose. In production, you would typically want to have the user accept the terms and conditions on first use and then store that in a retrievable location.
        TermsAndConditionsAccepted = true;

        bool useBearerToken = Agent365Agent.TryGetBearerTokenForDevelopment(out var bearerToken);
        string[] autoSignInHandlersForNotAgenticAuth = useBearerToken ? [] : new[] { MyAuthHandler };

        // Register Agentic specific Activity routes.  These will only be used if the incoming Activity is Agentic.
        this.OnAgentNotification("*", AgentNotificationActivityAsync, RouteRank.Last, autoSignInHandlers: new[] { AgenticIdAuthHandler });
        OnActivity(ActivityTypes.InstallationUpdate, OnHireMessageAsync, isAgenticOnly: true, autoSignInHandlers: new[] { AgenticIdAuthHandler });
        OnActivity(ActivityTypes.Message, MessageActivityAsync, rank: RouteRank.Last, isAgenticOnly: true, autoSignInHandlers: new[] { AgenticIdAuthHandler });
        OnActivity(ActivityTypes.Message, MessageActivityAsync, rank: RouteRank.Last, isAgenticOnly: false, autoSignInHandlers: autoSignInHandlersForNotAgenticAuth);
    }

    /// <summary>
    /// This processes messages sent to the agent from chat clients.
    /// </summary>
    /// <param name="turnContext"></param>
    /// <param name="turnState"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected async Task MessageActivityAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        string ObservabilityAuthHandlerName = "";
        string ToolAuthHandlerName = "";
        if (turnContext.IsAgenticRequest())
        {
            ObservabilityAuthHandlerName = AgenticIdAuthHandler;
            ToolAuthHandlerName = AgenticIdAuthHandler;
        }
        else
        {
            ObservabilityAuthHandlerName = MyAuthHandler;
            ToolAuthHandlerName = MyAuthHandler;
        }
        // Init the activity for observability

        // Always store the latest conversation reference so subscription creation can use it.
        _graphSubscriptionService.StoreConversationReference(
            turnContext.Activity.GetConversationReference());

        await A365OtelWrapper.InvokeObservedAgentOperation(
         "MessageProcessor",
         turnContext,
         turnState,
         _agentTokenCache,
         UserAuthorization,
         ObservabilityAuthHandlerName,
         _logger,
         async () =>
         {

             // Setup local service connection
             ServiceCollection serviceCollection = [
                        new ServiceDescriptor(typeof(ITurnState), turnState),
                            new ServiceDescriptor(typeof(ITurnContext), turnContext),
                            new ServiceDescriptor(typeof(Kernel), _kernel),
             ];

             // Disabled for development purposes. 
             //if (!IsApplicationInstalled)
             //{
             //    await turnContext.SendActivityAsync(MessageFactory.Text("Please install the application before sending messages."), cancellationToken);
             //    return;
             //}

             var agent365Agent = await GetAgent365Agent(serviceCollection, turnContext, ToolAuthHandlerName);
             if (!TermsAndConditionsAccepted)
             {
                 if (turnContext.Activity.ChannelId.Channel == Channels.Msteams)
                 {
                     var response = await agent365Agent.InvokeAgentAsync(turnContext.Activity.Text, new ChatHistory());
                     await OutputResponseAsync(turnContext, turnState, response, cancellationToken);
                     return;
                 }
             }

             // Check for direct skill invocation (bypasses trigger evaluation entirely).
             if (TryGetDirectSkillInvocation(turnContext.Activity?.Text, out var directSkillId))
             {
                 if (turnContext.StreamingResponse.IsStreamingChannel == true)
                     await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Running skill...", cancellationToken);
                 await ExecuteDirectSkillAsync(turnContext, agent365Agent, directSkillId, cancellationToken);
                 return;
             }

             if (turnContext.Activity.ChannelId.IsParentChannel(Channels.Msteams))
             {
                 await TeamsMessageActivityAsync(agent365Agent, turnContext, turnState, ToolAuthHandlerName, cancellationToken);
             }
             else if (turnContext.Activity.ChannelId.Channel == Channels.Emulator ||
                      turnContext.Activity.ChannelId.Channel == Channels.Test)
             {
                 var chatHistory = new ChatHistory();

                 // Evaluate triggers for this message by having the agent call the MCP tool
                 var eventData = _eventExtractor.ExtractMessageEventData(turnContext);
                 if (eventData != null)
                 {
                     try
                     {
                         // Build prompt for the agent to call evaluate_event_triggers MCP tool
                         var evaluationPrompt = _triggerEvaluationService.BuildTriggerEvaluationPrompt(eventData);
                         var evalChatHistory = new ChatHistory();

                         // Let the agent invoke the MCP tool. No turnContext so trigger eval
                         // JSON is never streamed to the user on streaming channels.
                         var evalResponse = await agent365Agent.InvokeAgentAsync(evaluationPrompt, evalChatHistory);

                         // Parse the agent's response
                         if (evalResponse?.Content != null)
                         {
                             var triggerResult = _triggerEvaluationService.ParseTriggerEvaluationResponse(evalResponse.Content);

                             // Apply task instructions to chat history if triggers matched
                             chatHistory.ApplyTriggerInstructions(triggerResult, turnContext, turnState, _logger, "Emulator/Test message");
                         }
                     }
                     catch (Exception ex)
                     {
                         _logger.LogWarning(ex, "Error during trigger evaluation for message. Proceeding without triggers.");
                     }
                 }

                 var response = await agent365Agent.InvokeAgentAsync(turnContext.Activity?.Text ?? string.Empty, chatHistory, turnContext);
                 await OutputResponseAsync(turnContext, turnState, response, cancellationToken);
             }
             else
             {
                 await turnContext.SendActivityAsync(MessageFactory.Text($"Sorry, I do not know how to respond to messages from channel '{turnContext.Activity.ChannelId}'."), cancellationToken);
             }
         }).ConfigureAwait(false);
    }

    /// <summary>
    /// This processes A365 Agent Notification Activities sent to the agent.
    /// </summary>
    /// <param name="turnContext"></param>
    /// <param name="turnState"></param>
    /// <param name="agentNotificationActivity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task AgentNotificationActivityAsync(ITurnContext turnContext, ITurnState turnState, AgentNotificationActivity agentNotificationActivity, CancellationToken cancellationToken)
    {

        string ObservabilityAuthHandlerName = "";
        string ToolAuthHandlerName = "";
        if (turnContext.IsAgenticRequest())
        {
            ObservabilityAuthHandlerName = AgenticIdAuthHandler;
            ToolAuthHandlerName = AgenticIdAuthHandler;
        }
        else
        {
            ObservabilityAuthHandlerName = MyAuthHandler;
            ToolAuthHandlerName = MyAuthHandler;
        }
        // Init the activity for observability
        await A365OtelWrapper.InvokeObservedAgentOperation(
         "AgentNotificationActivityAsync",
         turnContext,
         turnState,
         _agentTokenCache,
         UserAuthorization,
         ObservabilityAuthHandlerName,
         _logger,
         async () =>
         {
             // Setup local service connection
             ServiceCollection serviceCollection = [
                         new ServiceDescriptor(typeof(ITurnState), turnState),
                            new ServiceDescriptor(typeof(ITurnContext), turnContext),
                            new ServiceDescriptor(typeof(Kernel), _kernel),
                 ];

             //if (!IsApplicationInstalled)
             //{
             //    await turnContext.SendActivityAsync(MessageFactory.Text("Please install the application before sending notifications."), cancellationToken);
             //    return;
             //}

             var agent365Agent = await GetAgent365Agent(serviceCollection, turnContext, ToolAuthHandlerName);
             if (!TermsAndConditionsAccepted)
             {
                 var response = await agent365Agent.InvokeAgentAsync(turnContext.Activity.Text, new ChatHistory());
                 await OutputResponseAsync(turnContext, turnState, response, cancellationToken);
                 return;
             }

             // Step 1: Extract structured event data from the notification
             var eventData = _eventExtractor.ExtractEventData(turnContext, agentNotificationActivity);
             if (eventData == null)
             {
                 _logger.LogWarning(
                     "Failed to extract event data from notification type {NotificationType}. Using default behavior.",
                     agentNotificationActivity.NotificationType);
             }

             // Step 1b: For email notifications, enrich EventData with real subject/from/body
             // fetched from Graph — the Graph change notification only carries the message ID.
             if (eventData is EmailEventData emailEventData)
             {
                 var msgId = agentNotificationActivity.EmailNotification?.Id;
                 if (!string.IsNullOrEmpty(msgId))
                 {
                     var metadata = await _graphSubscriptionService.FetchEmailMetadataAsync(msgId, cancellationToken);
                     if (metadata != null)
                     {
                         emailEventData.Subject = metadata.Subject;
                         emailEventData.From = new EmailParticipant { Email = metadata.FromEmail, Name = metadata.FromName };
                         emailEventData.Body = metadata.BodyPreview;
                         _logger.LogInformation(
                             "Enriched email event data from Graph: Subject='{Subject}', From='{FromEmail}'",
                             metadata.Subject, metadata.FromEmail);
                     }
                     else
                     {
                         _logger.LogWarning(
                             "Could not fetch email metadata for message {MsgId}. Trigger conditions may not match.",
                             msgId);
                     }
                 }
             }

             // Step 2: Evaluate triggers by having the agent call the MCP tool
             TriggerEvaluationResponse triggerResult = TriggerEvaluationResponse.Empty;
             string? triggerInstructions = null;

             if (eventData != null)
             {
                 try
                 {
                     // Build prompt for the agent to call evaluate_event_triggers MCP tool
                     var evaluationPrompt = _triggerEvaluationService.BuildTriggerEvaluationPrompt(eventData);
                     var evalChatHistory = new ChatHistory();

                     // Show a brief status while evaluating — do NOT pass turnContext so the
                     // raw trigger-evaluation JSON is never streamed to the user on streaming channels.
                     if (turnContext.StreamingResponse.IsStreamingChannel == true)
                         await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Processing notification...", cancellationToken);
                     var evalResponse = await agent365Agent.InvokeAgentAsync(evaluationPrompt, evalChatHistory);

                     // Parse the agent's response
                     if (evalResponse?.Content != null)
                     {
                         _logger.LogInformation(
                             "Trigger eval raw LLM response (first 800 chars): {RawResponse}",
                             evalResponse.Content.Length > 800
                                 ? evalResponse.Content[..800] + "..."
                                 : evalResponse.Content);

                         triggerResult = _triggerEvaluationService.ParseTriggerEvaluationResponse(evalResponse.Content);

                         _logger.LogInformation(
                             "ParseTriggerEvaluationResponse: isActive={IsActive}, matchedCount={MatchedCount}, " +
                             "hasSkills={HasSkills}, skillsToExecute={SkillsToExecute}",
                             triggerResult.IsActive,
                             triggerResult.MatchedTriggerCount,
                             triggerResult.HasSkillsToExecute,
                             triggerResult.SkillsToExecute == null ? "null" : $"Length={triggerResult.SkillsToExecute.Length}");
                     }

                     // Step 3: Route based on V2 (skillsToExecute) or V1 (promptInstructions)
                     if (triggerResult.IsActive && triggerResult.HasSkillsToExecute)
                     {
                         // V2: Execute skill plan directly using the same clean prompt pattern as
                         // trigger evaluation. Bypass HandleEmailNotificationWithTriggersAsync —
                         // the skill plan IS the response.
                         _logger.LogInformation(
                             "Trigger evaluation matched (V2 skill plan) for {NotificationType}. {SkillCount} skills to execute.",
                             agentNotificationActivity.NotificationType,
                             triggerResult.SkillsToExecute!.Length);
                         await ExecuteV2SkillPlanAsync(
                             turnContext, agent365Agent, agentNotificationActivity,
                             triggerResult, eventData, cancellationToken);
                         return;
                     }
                     else if (triggerResult.IsActive && triggerResult.HasInstructions)
                     {
                         // V1: free-text prompt instructions
                         triggerInstructions = triggerResult.GetCombinedInstructions();
                         _logger.LogInformation(
                             "Trigger evaluation matched (V1 instructions) for {NotificationType}. Matched {MatchedCount} triggers.",
                             agentNotificationActivity.NotificationType,
                             triggerResult.MatchedTriggerCount);
                     }
                     else
                     {
                         _logger.LogDebug(
                             "No triggers matched for notification type {NotificationType}. Using default behavior.",
                             agentNotificationActivity.NotificationType);
                     }
                 }
                 catch (Exception ex)
                 {
                     _logger.LogWarning(ex, "Error during trigger evaluation. Using default behavior.");
                 }
             }

             switch (agentNotificationActivity.NotificationType)
             {
                 case NotificationTypeEnum.EmailNotification:
                     await HandleEmailNotificationWithTriggersAsync(
                         turnContext, agent365Agent, agentNotificationActivity, triggerInstructions, cancellationToken);
                     return;

                 case NotificationTypeEnum.WpxComment:
                     await HandleWpxCommentWithTriggersAsync(
                         turnContext, agent365Agent, agentNotificationActivity, triggerInstructions, cancellationToken);
                     return;

                 default:
                     // Handle other notification types with generic trigger-based processing
                     if (!string.IsNullOrEmpty(triggerInstructions))
                     {
                         await HandleGenericNotificationWithTriggersAsync(
                             turnContext, agent365Agent, agentNotificationActivity, triggerInstructions, cancellationToken);
                     }
                     else
                     {
                         _logger.LogWarning(
                             "Unhandled notification type {NotificationType} with no trigger instructions.",
                             agentNotificationActivity.NotificationType);
                         // Silently close the stream — no response shown to user.
                         if (turnContext.StreamingResponse.IsStreamingChannel == true)
                             await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
                     }
                     return;
             }
         }).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a V2 skill plan by calling mcp_WorkIQSandbox.execute_skill for each skill.
    /// Uses the pre-built query field from each SkillToExecute (synthesized by SkillQueryBuilder on the server).
    /// </summary>
    private async Task ExecuteV2SkillPlanAsync(
        ITurnContext turnContext,
        Agent365Agent agent365Agent,
        AgentNotificationActivity notificationActivity,
        TriggerEvaluationResponse triggerResult,
        NotificationEventData? eventData,
        CancellationToken cancellationToken)
    {
        Agent365AgentResponse? result = null;
        try
        {
            // Build event context enriched with real email metadata (fetched in Step 1b).
            var emailData = eventData as EmailEventData;
            var eventContextObj = new
            {
                emailId = notificationActivity.EmailNotification?.Id ?? string.Empty,
                fromEmail = emailData?.From?.Email ?? notificationActivity.From?.Id ?? string.Empty,
                fromName = emailData?.From?.Name ?? notificationActivity.From?.Name ?? string.Empty,
                subject = emailData?.Subject ?? string.Empty,
                bodyPreview = emailData?.Body ?? string.Empty
            };
            var eventContextJson = System.Text.Json.JsonSerializer.Serialize(eventContextObj);

            // Execute the first skill (lowest order). Single invocation keeps the prompt simple
            // and avoids multi-skill prompt complexity that caused empty-stream 400 errors.
            var skill = triggerResult.SkillsToExecute!.OrderBy(s => s.Order).First();
            var query = !string.IsNullOrEmpty(skill.Query)
                ? skill.Query
                : $"Execute skill '{skill.SkillId}'";

            var skillPrompt = $"""
Call the 'execute_skill' tool on mcp_WorkIQSandbox with:
- skillId: "{skill.SkillId}"
- query: "{query}"
- eventContextJson: {eventContextJson}

Present the result to the user in clear markdown. If the skill failed, explain what went wrong in plain language.
""";

            _logger.LogInformation("Executing V2 skill plan via mcp_WorkIQSandbox.execute_skill. SkillId: {SkillId}", skill.SkillId);

            result = await agent365Agent.InvokeAgentAsync(skillPrompt, new ChatHistory(), turnContext);

            _logger.LogInformation("V2 skill plan execution result: {Result}", result?.Content);

            // Send the formatted skill result to the user (non-streaming channels).
            // For streaming channels, InvokeAgentAsync already queued chunks during invocation.
            if (!string.IsNullOrWhiteSpace(result?.Content) && turnContext.StreamingResponse.IsStreamingChannel != true)
            {
                IActivity responseActivity = notificationActivity.NotificationType == NotificationTypeEnum.EmailNotification
                    ? EmailResponse.CreateEmailResponseActivity(result.Content)
                    : MessageFactory.Text(result.Content);
                await turnContext.SendActivityAsync(responseActivity, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing V2 skill plan for notification type {NotificationType}",
                notificationActivity.NotificationType);
        }
        finally
        {
            if (turnContext.StreamingResponse.IsStreamingChannel == true)
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Handles email notifications with trigger-based personalized instructions.
    /// </summary>
    private async Task HandleEmailNotificationWithTriggersAsync(
        ITurnContext turnContext,
        Agent365Agent agent365Agent,
        AgentNotificationActivity agentNotificationActivity,
        string? triggerInstructions,
        CancellationToken cancellationToken)
    {
        try
        {
            if (agentNotificationActivity.EmailNotification == null)
            {
                var notFoundEmailActivity = EmailResponse.CreateEmailResponseActivity("I could not find the email notification details.");
                await turnContext.SendActivityAsync(notFoundEmailActivity, cancellationToken);
                return;
            }

            var chatHistory = new ChatHistory();

            // First, retrieve the email content
            var emailContent = await agent365Agent.InvokeAgentAsync(
                $"You have a new email from {agentNotificationActivity.From.Name} with id '{agentNotificationActivity.EmailNotification.Id}', " +
                $"ConversationId '{agentNotificationActivity.EmailNotification.ConversationId}'. Please retrieve this message and return it in text format.",
                chatHistory);

            // Build the processing prompt based on whether we have trigger instructions
            string processingPrompt;
            if (!string.IsNullOrEmpty(triggerInstructions))
            {
                // Present context first, then instructions
                // Instructions may include actions beyond just responding (e.g., "also send email to manager")
                processingPrompt = $"""
                    CONTEXT:
                    You received an email from {agentNotificationActivity.From.Name}.

                    EMAIL CONTENT:
                    {emailContent.Content}

                    TASK INSTRUCTIONS:
                    {triggerInstructions}

                    Execute all instructions above. This may include responding to the email AND/OR taking other actions (sending emails, creating tasks, etc.).
                    """;
                _logger.LogInformation("Processing email with task instructions");
            }
            else
            {
                // Fall back to default behavior
                processingPrompt = $"You have received the following email. Please follow any instructions in it. {emailContent.Content}";
            }

            var response = await agent365Agent.InvokeAgentAsync(processingPrompt, chatHistory);
            response ??= new Agent365AgentResponse
            {
                Content = "I have processed your email but do not have a response at this time.",
                ContentType = Agent365AgentResponseContentType.Text
            };

            var responseEmailActivity = EmailResponse.CreateEmailResponseActivity(response.Content!);
            await turnContext.SendActivityAsync(responseEmailActivity, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "There was an error processing the email notification");
            var errorEmailActivity = EmailResponse.CreateEmailResponseActivity("Unable to process your email at this time.");
            await turnContext.SendActivityAsync(errorEmailActivity, cancellationToken);
        }
        finally
        {
            // End the stream on streaming channels — without this, the platform auto-flushes
            // the unclosed stream with an empty activity → 400 BadRequest.
            if (turnContext.StreamingResponse.IsStreamingChannel == true)
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Handles WPX (Word) comment notifications with trigger-based personalized instructions.
    /// </summary>
    private async Task HandleWpxCommentWithTriggersAsync(
        ITurnContext turnContext,
        Agent365Agent agent365Agent,
        AgentNotificationActivity agentNotificationActivity,
        string? triggerInstructions,
        CancellationToken cancellationToken)
    {
        try
        {
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync(
                "Thanks for the Word notification! Working on a response...", cancellationToken);

            if (agentNotificationActivity.WpxCommentNotification == null)
            {
                turnContext.StreamingResponse.QueueTextChunk("I could not find the Word notification details.");
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
                return;
            }

            var driveId = "default";
            var chatHistory = new ChatHistory();

            // Retrieve the Word document and comments
            var wordContent = await agent365Agent.InvokeAgentAsync(
                $"You have a new comment on the Word document with id '{agentNotificationActivity.WpxCommentNotification.DocumentId}', " +
                $"comment id '{agentNotificationActivity.WpxCommentNotification.ParentCommentId}', drive id '{driveId}'. " +
                "Please retrieve the Word document as well as the comments in the Word document and return it in text format.",
                chatHistory);

            var commentToAgent = agentNotificationActivity.Text;

            // Build the processing prompt based on whether we have trigger instructions
            string processingPrompt;
            if (!string.IsNullOrEmpty(triggerInstructions))
            {
                // Present context first, then instructions
                // Instructions may include actions beyond just responding (e.g., "also send email to manager")
                processingPrompt = $"""
                    CONTEXT:
                    You received a comment on a Word document from {agentNotificationActivity.From?.Name ?? "a user"}.

                    DOCUMENT CONTENT:
                    {wordContent.Content}

                    COMMENT TO RESPOND TO:
                    {commentToAgent}

                    TASK INSTRUCTIONS:
                    {triggerInstructions}

                    Execute all instructions above. This may include responding to the comment AND/OR taking other actions (sending emails, notifying others, etc.).
                    """;
                _logger.LogInformation("Processing Word comment with task instructions");
            }
            else
            {
                // Fall back to default behavior
                processingPrompt = $"You have received the following Word document content and comments. " +
                    $"Please refer to these when responding to comment '{commentToAgent}'. {wordContent.Content}";
            }

            var response = await agent365Agent.InvokeAgentAsync(processingPrompt, chatHistory);
            var responseWpxActivity = MessageFactory.Text(response.Content!);
            await turnContext.SendActivityAsync(responseWpxActivity, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "There was an error processing the mention notification");
            var responseWpxActivity = MessageFactory.Text("Unable to process your mention comment at this time.");
            await turnContext.SendActivityAsync(responseWpxActivity, cancellationToken);
        }
        finally
        {
            // End the stream on streaming channels — without this, the platform auto-flushes
            // the unclosed stream with an empty activity → 400 BadRequest.
            if (turnContext.StreamingResponse.IsStreamingChannel == true)
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Handles generic notifications using trigger-based instructions when no specific handler exists.
    /// </summary>
    private async Task HandleGenericNotificationWithTriggersAsync(
        ITurnContext turnContext,
        Agent365Agent agent365Agent,
        AgentNotificationActivity agentNotificationActivity,
        string triggerInstructions,
        CancellationToken cancellationToken)
    {
        try
        {
            var chatHistory = new ChatHistory();

            // Present context first, then instructions
            // Instructions may include actions beyond just processing the notification
            var prompt = $"""
                CONTEXT:
                You received a notification.

                NOTIFICATION TYPE: {agentNotificationActivity.NotificationType}
                FROM: {agentNotificationActivity.From?.Name ?? "Unknown"}
                CONTENT: {turnContext.Activity?.Text ?? string.Empty}

                TASK INSTRUCTIONS:
                {triggerInstructions}

                Execute all instructions above. This may include responding to the notification AND/OR taking other actions.
                """;

            _logger.LogInformation(
                "Processing notification type {NotificationType} with task instructions",
                agentNotificationActivity.NotificationType);

            var response = await agent365Agent.InvokeAgentAsync(prompt, chatHistory);

            if (response != null && !string.IsNullOrEmpty(response.Content))
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(response.Content), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing generic notification with triggers");
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Unable to process your notification at this time."),
                cancellationToken);
        }
        finally
        {
            // End the stream on streaming channels — without this, the platform auto-flushes
            // the unclosed stream with an empty activity → 400 BadRequest.
            if (turnContext.StreamingResponse.IsStreamingChannel == true)
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }


    /// <summary>
    /// Process Agent Onboard Event.
    /// </summary>
    /// <param name="turnContext"></param>
    /// <param name="turnState"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected async Task OnHireMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        string ObservabilityAuthHandlerName = "";
        if (turnContext.IsAgenticRequest())
        {
            ObservabilityAuthHandlerName = AgenticIdAuthHandler;
        }
        else
        {
            ObservabilityAuthHandlerName = MyAuthHandler;
        }
        // Init the activity for observability
        await A365OtelWrapper.InvokeObservedAgentOperation(
         "OnHireMessageAsync",
         turnContext,
         turnState,
         _agentTokenCache,
         UserAuthorization,
         ObservabilityAuthHandlerName,
         _logger,
         async () =>
         {

             if (turnContext.Activity.Action == InstallationUpdateActionTypes.Add)
             {
                 IsApplicationInstalled = true;
                 TermsAndConditionsAccepted = turnContext.IsAgenticRequest() ? true : false;

                 // Create a Microsoft Graph mail subscription so new emails are forwarded
                 // to this agent as AgentNotificationActivity via /api/graph/notifications.
                 try
                 {
                     // Store the install conversation reference before creating the subscription.
                     _graphSubscriptionService.StoreConversationReference(
                         turnContext.Activity.GetConversationReference());

                     // Try to get a delegated token first; if unavailable (e.g. bearer-token dev mode)
                     // CreateSubscriptionAsync will fall back to client credentials automatically.
                     var graphToken = await UserAuthorization.GetTurnTokenAsync(
                         turnContext, AgenticIdAuthHandler);

                     var convRef = turnContext.Activity.GetConversationReference();
                     var subscriptionId = await _graphSubscriptionService.CreateSubscriptionAsync(
                         graphToken ?? string.Empty, convRef, cancellationToken);

                     if (subscriptionId != null)
                     {
                         _logger.LogInformation(
                             "Created Graph mail subscription {SubscriptionId} on agent install.",
                             subscriptionId);
                     }
                 }
                 catch (Exception ex)
                 {
                     // Non-fatal: the agent still functions, it just won't proactively forward emails.
                     _logger.LogError(ex,
                         "Failed to create Graph mail subscription on install. " +
                         "Ensure GraphSubscription:UserMailbox is set and the app has Mail.Read " +
                         "application permission with admin consent.");
                 }

                 string message = $"Thank you for hiring me! Looking forward to assisting you in your professional journey!";
                 if (!turnContext.IsAgenticRequest())
                 {
                     message += "Before I begin, could you please confirm that you accept the terms and conditions?";
                 }

                 await turnContext.SendActivityAsync(MessageFactory.Text(message), cancellationToken);
             }
             else if (turnContext.Activity.Action == InstallationUpdateActionTypes.Remove)
             {
                 IsApplicationInstalled = false;
                 TermsAndConditionsAccepted = false;

                 // Delete all tracked Graph subscriptions on uninstall.
                 try
                 {
                     var graphToken = await UserAuthorization.GetTurnTokenAsync(
                         turnContext, AgenticIdAuthHandler);

                     if (!string.IsNullOrEmpty(graphToken))
                     {
                         foreach (var record in _graphSubscriptionService.GetAllSubscriptions())
                         {
                             await _graphSubscriptionService.DeleteSubscriptionAsync(
                                 graphToken, record.SubscriptionId, cancellationToken);
                         }
                     }
                 }
                 catch (Exception ex)
                 {
                     _logger.LogWarning(ex, "Error deleting Graph subscriptions on uninstall.");
                 }

                 await turnContext.SendActivityAsync(MessageFactory.Text("Thank you for your time, I enjoyed working with you."), cancellationToken);
             }
         }).ConfigureAwait(false);
    }

    /// <summary>
    /// This is the specific handler for teams messages sent to the agent from Teams chat clients.
    /// </summary>
    /// <param name="agent365Agent"></param>
    /// <param name="turnContext"></param>
    /// <param name="turnState"></param>
    /// <param name="authHandlerName">The authentication handler name for trigger evaluation.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected async Task TeamsMessageActivityAsync(Agent365Agent agent365Agent, ITurnContext turnContext, ITurnState turnState, string authHandlerName, CancellationToken cancellationToken)
    {
        // Start a Streaming Process
        await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Working on a response for you", cancellationToken);
        try
        {
            ChatHistory chatHistory = turnState.GetValue("conversation.chatHistory", () => new ChatHistory());

            // Evaluate triggers for this message by having the agent call the MCP tool
            var eventData = _eventExtractor.ExtractMessageEventData(turnContext);
            if (eventData != null)
            {
                try
                {
                    // Build prompt for the agent to call evaluate_event_triggers MCP tool
                    var evaluationPrompt = _triggerEvaluationService.BuildTriggerEvaluationPrompt(eventData);
                    var evalChatHistory = new ChatHistory();

                    // Let the agent invoke the MCP tool. No turnContext so trigger eval
                    // JSON is never streamed to the user on streaming channels.
                    var evalResponse = await agent365Agent.InvokeAgentAsync(evaluationPrompt, evalChatHistory);

                    // Parse the agent's response
                    if (evalResponse?.Content != null)
                    {
                        var triggerResult = _triggerEvaluationService.ParseTriggerEvaluationResponse(evalResponse.Content);

                        // Apply task instructions to chat history if triggers matched
                        // Uses turnState to prevent duplicates across multiple turns
                        chatHistory.ApplyTriggerInstructions(triggerResult, turnContext, turnState, _logger, "Teams message");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during trigger evaluation for Teams message. Proceeding without triggers.");
                }
            }

            // Invoke the Agent365Agent to process the message
            Agent365AgentResponse response = await agent365Agent.InvokeAgentAsync(turnContext.Activity?.Text ?? string.Empty, chatHistory, turnContext);

        }
        finally
        {
            await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }

    protected async Task OutputResponseAsync(ITurnContext turnContext, ITurnState turnState, Agent365AgentResponse response, CancellationToken cancellationToken)
    {
        if (response == null)
        {
            await turnContext.SendActivityAsync("Sorry, I couldn't get an answer at the moment.");
            return;
        }

        // Create a response message based on the response content type from the Agent365Agent
        // Send the response message back to the user. 
        switch (response.ContentType)
        {
            case Agent365AgentResponseContentType.Text:
                if (!string.IsNullOrEmpty(response.Content))
                    await turnContext.SendActivityAsync(response.Content!);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Entry point called by <see cref="Controllers.GraphNotificationController"/> to route a
    /// Graph-sourced email change notification through the existing notification processing pipeline.
    /// This method creates a fresh <see cref="ITurnState"/> for the proactive turn and delegates
    /// to the standard <see cref="AgentNotificationActivityAsync"/> handler so that trigger
    /// evaluation and skill execution run identically to platform-delivered notifications.
    /// </summary>
    internal async Task RouteGraphEmailNotificationAsync(
        ITurnContext turnContext,
        AgentNotificationActivity notificationActivity,
        CancellationToken cancellationToken)
    {
        // Create a fresh TurnState for this proactive turn.
        var turnState = new TurnState();

        await AgentNotificationActivityAsync(
            turnContext,
            turnState,
            notificationActivity,
            cancellationToken);
    }

    /// <summary>
    /// Sets up an in context instance of the Agent365Agent..
    /// </summary>
    /// <param name="serviceCollection"></param>
    /// <param name="turnContext"></param>
    /// <param name="authHandlerName"></param>
    /// <returns></returns>
    private async Task<Agent365Agent> GetAgent365Agent(ServiceCollection serviceCollection, ITurnContext turnContext, string authHandlerName)
    {
        return await Agent365Agent.CreateA365AgentWrapper(_kernel, serviceCollection.BuildServiceProvider(), _toolsService, authHandlerName, UserAuthorization, turnContext, _configuration).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects whether the user's message is a direct skill invocation phrase and returns the skill ID.
    /// Bypasses trigger evaluation — the skill is called directly on mcp_SkillsExecutorServer.
    /// </summary>
    private static bool TryGetDirectSkillInvocation(string? messageText, out string skillId)
    {
        skillId = string.Empty;
        if (string.IsNullOrWhiteSpace(messageText)) return false;

        var text = messageText.Trim().ToLowerInvariant();

        // Do NOT intercept if the user is asking to SET UP a trigger/personalization rule.
        // Conditional phrases indicate the user wants the task personalization server to
        // register a trigger, not run the skill immediately.
        bool isTriggerSetupRequest =
            text.StartsWith("when ") || text.StartsWith("whenever ") ||
            text.StartsWith("if ") || text.StartsWith("every time ") ||
            text.Contains(" when i ") || text.Contains(" whenever i ") ||
            text.Contains(" if i ") || text.Contains(" every time i ") ||
            text.Contains("set up") || text.Contains("setup") ||
            text.Contains("configure") || text.Contains("personali");

        if (isTriggerSetupRequest) return false;

        if (text.Contains("self introspection") || text.Contains("self-introspection") || text.Contains("run introspection"))
        {
            skillId = "run_self_introspection";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Executes a named skill directly via mcp_WorkIQSandbox, bypassing the task personalization server.
    /// Uses execute_skill with a natural language query for both streaming and non-streaming channels.
    /// </summary>
    private async Task ExecuteDirectSkillAsync(
        ITurnContext turnContext,
        Agent365Agent agent365Agent,
        string skillId,
        CancellationToken cancellationToken)
    {
        try
        {
            var isStreaming = turnContext.StreamingResponse.IsStreamingChannel == true;

            // Always use execute_skill (non-streaming MCP tool). The streaming variant is not
            // supported by the workIQ backend. InvokeAgentAsync already streams LLM response
            // chunks to the user via QueueTextChunk on streaming channels.
            var prompt = $@"Call the 'execute_skill' tool on mcp_WorkIQSandbox with:
- skillId: ""{skillId}""
- query: ""Run the {skillId} skill""
- eventContextJson: ""{{}}""

If the skill succeeds, present only the skill output content in clean markdown format. Do not include execution metadata.
If the skill fails, report the error so the user can diagnose the problem.";

            _logger.LogInformation("Executing direct skill invocation: {SkillId} via execute_skill", skillId);

            var result = await agent365Agent.InvokeAgentAsync(prompt, new ChatHistory(), turnContext);

            _logger.LogInformation("Direct skill execution result: {Content}", result?.Content);

            if (result?.Content is not null && !isStreaming)
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(result.Content), cancellationToken);
            }
        }
        finally
        {
            if (turnContext.StreamingResponse.IsStreamingChannel == true)
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
        }
    }
}
