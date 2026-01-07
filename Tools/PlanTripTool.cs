// =============================================================================
// PlanTripTool.cs - AI Agent Tool for Orchestration Invocation
// =============================================================================
// This tool bridges the conversational agent with the Durable Functions
// orchestration. When the conversational agent has collected all required
// information from the user, it calls PlanTrip to start the orchestration.
//
// Uses DurableAgentContext.Current to schedule orchestrations from within
// tool calls, following the long-running tools pattern from the agent framework.
// =============================================================================

// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.Hosting.AzureFunctions;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Functions;
using TravelPlannerFunctions.Models;

namespace TravelPlannerFunctions.Tools;

/// <summary>
/// AI agent tool that enables the conversational agent to start the travel
/// planning orchestration once all required information has been collected.
/// Uses DurableAgentContext.Current to access orchestration scheduling from tool calls.
/// </summary>
public sealed class PlanTripTool
{
    // =========================================================================
    // Static Context for Orchestration Tracking
    // =========================================================================
    
    /// <summary>
    /// Thread-safe dictionary that tracks conversations where an orchestration was scheduled.
    /// The response handler checks this to determine whether to send the "done" marker
    /// (if no orchestration) or let the orchestration send it (if one was scheduled).
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> _orchestrationsScheduled = new();
    
    /// <summary>
    /// Marks that an orchestration was scheduled for the given conversation.
    /// The response handler should NOT send "done" for this conversation.
    /// </summary>
    public static void MarkOrchestrationScheduled(string conversationId)
    {
        _orchestrationsScheduled[conversationId] = true;
    }
    
    /// <summary>
    /// Checks if an orchestration was scheduled for the given conversation
    /// and clears the flag if it was set.
    /// </summary>
    /// <returns>True if an orchestration was scheduled; false otherwise.</returns>
    public static bool ConsumeOrchestrationScheduled(string conversationId)
    {
        return _orchestrationsScheduled.TryRemove(conversationId, out _);
    }

    // =========================================================================
    // Dependencies (injected via constructor)
    // =========================================================================
    
    private readonly ILogger<PlanTripTool> _logger;
    
    /// <summary>
    /// Creates a new instance of PlanTripTool with required dependencies.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PlanTripTool(ILogger<PlanTripTool> logger)
    {
        _logger = logger;
    }

    // =========================================================================
    // AI Agent Tool Methods
    // =========================================================================
    // These methods are exposed to the AI agent via [Description] attributes.
    // The agent calls these when it determines user intent matches the tool.
    // Uses DurableAgentContext.Current to schedule orchestrations.
    // =========================================================================

    /// <summary>
    /// Plans a trip by starting the travel planner orchestration with the collected information.
    /// Call this function when you have gathered all required travel details from the user.
    /// </summary>
    [Description("Plans a trip by starting the travel planning process. Call this when you have collected: user name, travel preferences, duration in days, budget, travel dates, and any special requirements from the user.")]
    public string PlanTrip(
        [Description("The traveler's full name")] string userName,
        [Description("Detailed travel preferences describing the type of vacation desired")] string preferences,
        [Description("Number of days for the trip (minimum 1, maximum 30)")] int durationInDays,
        [Description("Budget for the trip including currency and amount")] string budget,
        [Description("Preferred travel dates or date range")] string travelDates,
        [Description("Special requirements like dietary restrictions, accessibility needs, or travel companions")] string specialRequirements = "")
    {
        _logger.LogInformation("PlanTrip tool called for user {UserName}", userName);

        // Validate inputs
        if (string.IsNullOrWhiteSpace(userName))
        {
            return "I need your name to create the travel plan. What's your name?";
        }

        if (string.IsNullOrWhiteSpace(preferences))
        {
            return "I need to know your travel preferences. What kind of trip are you looking for?";
        }

        if (durationInDays < 1 || durationInDays > 30)
        {
            return "The trip duration should be between 1 and 30 days. How many days would you like to travel?";
        }

        if (string.IsNullOrWhiteSpace(budget))
        {
            return "I need to know your budget to plan appropriately. What's your budget for this trip?";
        }

        if (string.IsNullOrWhiteSpace(travelDates))
        {
            return "When would you like to travel? Please provide your preferred travel dates.";
        }

        try
        {
            // Get the conversation ID from the current agent thread context
            // This is set by the durable agent framework before invoking tool calls
            string? conversationId = DurableAgentContext.Current?.CurrentThread
                .GetService<AgentThreadMetadata>()?.ConversationId;

            _logger.LogInformation(
                "PlanTrip tool called with conversation ID: {ConversationId}",
                conversationId ?? "null");

            // Create the travel request
            var travelRequest = new TravelRequest(
                UserName: userName,
                Preferences: preferences,
                DurationInDays: durationInDays,
                Budget: budget,
                TravelDates: travelDates,
                SpecialRequirements: specialRequirements ?? "",
                ConversationId: conversationId
            );

            _logger.LogInformation(
                "Starting travel planner orchestration for user {UserName} with conversation {ConversationId}", 
                userName, conversationId);

            // Schedule the orchestration using DurableAgentContext.Current
            // This will start running after the tool call completes
            string instanceId = DurableAgentContext.Current.ScheduleNewOrchestration(
                name: nameof(TravelPlannerOrchestrator.RunTravelPlannerOrchestration),
                input: travelRequest);

            // Mark that an orchestration was scheduled for this conversation
            // The response handler will NOT send "done" so the client keeps listening
            if (!string.IsNullOrEmpty(conversationId))
            {
                MarkOrchestrationScheduled(conversationId);
                _logger.LogInformation(
                    "Marked orchestration scheduled for conversation {ConversationId}",
                    conversationId);
            }

            _logger.LogInformation(
                "Travel planner orchestration scheduled for user '{UserName}' with instance ID: {InstanceId}",
                userName,
                instanceId);

            return $"🎉 **Great news!** I've started planning your trip!\n" +
                $"\n" +
                $"**Trip Details:**\n" +
                $"- **Name:** {userName}\n" +
                $"- **Duration:** {durationInDays} days\n" +
                $"- **Budget:** {budget}\n" +
                $"- **Dates:** {travelDates}\n" +
                $"- **Preferences:** {preferences}\n" +
                (string.IsNullOrWhiteSpace(specialRequirements) ? "" : $"- **Special Requirements:** {specialRequirements}\n") +
                $"\n" +
                $"**Orchestration ID:** `{instanceId}`\n" +
                $"\n" +
                $"I'm now:\n" +
                $"1. 🔍 Finding the best destination matches for your preferences\n" +
                $"2. 📅 Creating a day-by-day itinerary\n" +
                $"3. 🍽️ Gathering local restaurant and attraction recommendations\n" +
                $"4. 💰 Calculating costs in local and your budget currency\n" +
                $"\n" +
                $"This typically takes 1-2 minutes. I'll update you as each step completes!\n" +
                $"\n" +
                $"You can check the status at any time by asking about orchestration ID: `{instanceId}`";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start travel planner orchestration");
            return $"I encountered an error while starting the travel planning process: {ex.Message}. Please try again.";
        }
    }

    /// <summary>
    /// Checks the status of an ongoing trip planning orchestration.
    /// </summary>
    [Description("Checks the status of an ongoing trip planning process. Use this when the user asks about the status of their travel plan.")]
    public async Task<string> CheckTripPlanningStatus(
        [Description("The orchestration instance ID returned when planning started")] string instanceId)
    {
        _logger.LogInformation("Checking status for workflow instance: {InstanceId}", instanceId);

        try
        {
            OrchestrationMetadata? status = await DurableAgentContext.Current.GetOrchestrationStatusAsync(
                instanceId,
                true);

            if (status == null)
            {
                _logger.LogInformation("Workflow instance '{InstanceId}' not found.", instanceId);
                return $"I couldn't find a travel plan with ID `{instanceId}`. Please check the ID and try again.";
            }

            var runtimeStatus = status.RuntimeStatus.ToString();
            
            string progressInfo = "";
            if (status.SerializedCustomStatus != null)
            {
                try
                {
                    var customStatus = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(status.SerializedCustomStatus);
                    if (customStatus != null)
                    {
                        if (customStatus.TryGetValue("step", out var step))
                            progressInfo += $"\n- **Current Step:** {step}";
                        if (customStatus.TryGetValue("message", out var message))
                            progressInfo += $"\n- **Status:** {message}";
                        if (customStatus.TryGetValue("progress", out var progress))
                            progressInfo += $"\n- **Progress:** {progress}%";
                        if (customStatus.TryGetValue("destination", out var dest))
                            progressInfo += $"\n- **Selected Destination:** {dest}";
                    }
                }
                catch { }
            }

            string statusEmoji = runtimeStatus switch
            {
                "Running" => "🔄",
                "Completed" => "✅",
                "Failed" => "❌",
                "Pending" => "⏳",
                _ => "📋"
            };

            string response = $"{statusEmoji} **Trip Planning Status**\n" +
                $"\n" +
                $"**Instance ID:** `{instanceId}`\n" +
                $"**Status:** {runtimeStatus}\n" +
                $"**Started:** {status.CreatedAt:g}\n" +
                $"**Last Updated:** {status.LastUpdatedAt:g}\n" +
                progressInfo;

            if (progressInfo.Contains("WaitingForApproval"))
            {
                response += "\n\n🔔 **Action Required:** Your travel plan is ready for review!";
            }

            if (runtimeStatus == "Completed" && status.SerializedOutput != null)
            {
                response += "\n\n✨ **Your travel plan is complete!**";
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check orchestration status for {InstanceId}", instanceId);
            return $"I encountered an error checking the status: {ex.Message}";
        }
    }

    /// <summary>
    /// Approves or rejects a travel plan that is awaiting user confirmation.
    /// </summary>
    [Description("Approves or rejects a travel plan that is waiting for user approval. Call when the user wants to approve or reject their travel plan.")]
    public async Task<string> RespondToTravelPlan(
        [Description("The orchestration instance ID")] string instanceId,
        [Description("Whether to approve (true) or reject (false) the travel plan")] bool approved,
        [Description("Optional comments about the decision")] string comments = "")
    {
        _logger.LogInformation(
            "Processing approval response for workflow instance: {InstanceId}, Approved: {Approved}", 
            instanceId, approved);

        try
        {
            var approvalResponse = new ApprovalResponse(approved, comments);
            
            await DurableAgentContext.Current.RaiseOrchestrationEventAsync(
                instanceId, 
                "ApprovalEvent", 
                approvalResponse);

            if (approved)
            {
                _logger.LogInformation("Approval submitted for {InstanceId}", instanceId);
                
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);
                    var status = await DurableAgentContext.Current.GetOrchestrationStatusAsync(
                        instanceId, 
                        true);
                    
                    if (status?.RuntimeStatus == OrchestrationRuntimeStatus.Completed)
                    {
                        if (status.SerializedOutput != null)
                        {
                            try
                            {
                                var result = JsonSerializer.Deserialize<TravelPlanResult>(status.SerializedOutput);
                                if (result != null && !string.IsNullOrEmpty(result.BookingConfirmation))
                                {
                                    return $"✅ **Travel Plan Approved & Booked!**\n" +
                                        $"\n" +
                                        $"🎫 **{result.BookingConfirmation}**\n" +
                                        $"\n" +
                                        (string.IsNullOrWhiteSpace(comments) ? "" : $"**Your notes:** {comments}\n") +
                                        $"\n" +
                                        $"Enjoy your trip! 🌴✈️";
                                }
                            }
                            catch { }
                        }
                        
                        return $"✅ **Travel Plan Approved & Booked!**\n" +
                            $"\n" +
                            $"Your trip has been successfully booked!\n" +
                            (string.IsNullOrWhiteSpace(comments) ? "" : $"**Your notes:** {comments}\n") +
                            $"\n" +
                            $"Enjoy your trip! 🌴✈️";
                    }
                    else if (status?.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
                    {
                        return "✅ **Travel Plan Approved** but there was an issue completing the booking.";
                    }
                }
                
                return $"✅ **Travel Plan Approved!**\n" +
                    $"\n" +
                    $"Your approval was submitted and the booking is being processed.\n" +
                    $"Check status with ID: `{instanceId}`";
            }
            else
            {
                return $"❌ **Travel Plan Not Approved**\n" +
                    $"\n" +
                    $"I've recorded your decision.\n" +
                    (string.IsNullOrWhiteSpace(comments) ? "" : $"**Your feedback:** {comments}\n") +
                    $"\n" +
                    $"Would you like me to help you plan a different trip?";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send approval response for {InstanceId}", instanceId);
            return $"I encountered an error processing your response: {ex.Message}. Please try again.";
        }
    }
}
