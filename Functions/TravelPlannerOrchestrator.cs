// =============================================================================
// TravelPlannerOrchestrator.cs - Durable Functions Orchestration
// =============================================================================
// This orchestrator coordinates the multi-agent travel planning workflow.
// It demonstrates several Durable Functions patterns:
// - Fan-out/Fan-in: Parallel execution of itinerary and recommendations agents
// - Human Interaction: Waiting for user approval before booking
// - External Events: Receiving approval/rejection responses
// - Durable Agents: AI-powered agents for specialized tasks
// =============================================================================

using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Models;

namespace TravelPlannerFunctions.Functions;

/// <summary>
/// Orchestrates the complete travel planning workflow using multiple AI agents.
/// </summary>
public class TravelPlannerOrchestrator
{
    // =========================================================================
    // Constants
    // =========================================================================
    
    /// <summary>Days to wait for user approval before timing out.</summary>
    private const int ApprovalTimeoutDays = 7;
    
    /// <summary>Maximum size for orchestration custom status (Azure limit).</summary>
    private const int StatusMaxSizeBytes = 16_384; // 16KB

    // Progress milestone percentages for UI display
    private const int ProgressStarting = 0;
    private const int ProgressDestinations = 10;
    private const int ProgressItinerary = 30;
    private const int ProgressLocalRecommendations = 50;
    private const int ProgressSavingPlan = 70;
    private const int ProgressRequestingApproval = 85;
    private const int ProgressWaitingForApproval = 90;
    private const int ProgressBooking = 95;
    
    // =========================================================================
    // Constructor
    // =========================================================================
    
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes the orchestrator with logging support.
    /// </summary>
    public TravelPlannerOrchestrator(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<TravelPlannerOrchestrator>();
    }

    // =========================================================================
    // Helper Methods
    // =========================================================================
    
    /// <summary>
    /// Streams a progress update to the conversation if a conversation ID is provided.
    /// Progress updates appear in the chat UI in real-time.
    /// </summary>
    private static async Task StreamProgressAsync(
        TaskOrchestrationContext context, 
        string? conversationId, 
        string message)
    {
        if (!string.IsNullOrEmpty(conversationId))
        {
            await context.CallActivityAsync(
                nameof(TravelPlannerActivities.StreamProgressUpdate),
                new ProgressUpdateRequest(conversationId, message));
        }
    }

    // =========================================================================
    // Main Orchestration Function
    // =========================================================================
    
    /// <summary>
    /// The main orchestration function that coordinates the travel planning workflow.
    /// </summary>
    /// <remarks>
    /// Workflow Steps:
    /// 1. Get destination recommendations based on user preferences
    /// 2. Create detailed itinerary (parallel with step 3)
    /// 3. Get local recommendations (parallel with step 2)
    /// 4. Save travel plan to blob storage
    /// 5. Request user approval (Human-in-the-Loop)
    /// 6. Wait for approval event
    /// 7. If approved, book the trip
    /// </remarks>
    /// <param name="context">The orchestration context from Durable Functions.</param>
    /// <returns>The complete travel plan result with booking confirmation if approved.</returns>
    [Function(nameof(RunTravelPlannerOrchestration))]
    public async Task<TravelPlanResult> RunTravelPlannerOrchestration(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var travelRequest = context.GetInput<TravelRequest>()
            ?? throw new ArgumentNullException(nameof(context), "Travel request input is required");

        var logger = context.CreateReplaySafeLogger<TravelPlannerOrchestrator>();
        logger.LogInformation(
            "Starting travel planning orchestration for user {UserName}", 
            travelRequest.UserName);

        // Stream initial progress to the conversation
        await StreamProgressAsync(context, travelRequest.ConversationId,
            "\n\n🚀 **Starting your trip planning!**\n");

        // Set initial status
        SetOrchestrationStatus(context, "Starting", 
            $"Starting travel planning for {travelRequest.UserName}", 
            ProgressStarting);

        // -----------------------------------------------------------------
        // Initialize AI Agents
        // -----------------------------------------------------------------
        // Each agent is specialized for a specific task in the workflow.
        // Agents are durable and can be replayed safely.
        // -----------------------------------------------------------------
        DurableAIAgent destinationAgent = context.GetAgent("DestinationRecommenderAgent");
        DurableAIAgent itineraryAgent = context.GetAgent("ItineraryPlannerAgent");
        DurableAIAgent localRecommendationsAgent = context.GetAgent("LocalRecommendationsAgent");

        // Create new threads for each agent
        AgentThread destinationThread = destinationAgent.GetNewThread();
        AgentThread itineraryThread = itineraryAgent.GetNewThread();
        AgentThread localThread = localRecommendationsAgent.GetNewThread();

        // -----------------------------------------------------------------
        // Step 1: Get Destination Recommendations
        // -----------------------------------------------------------------
        logger.LogInformation("Step 1: Requesting destination recommendations");
        await StreamProgressAsync(context, travelRequest.ConversationId,
            "🔍 **Step 1/5:** Finding the best destinations for your preferences...\n");
        SetOrchestrationStatus(context, "GetDestinationRecommendations",
            "Finding the perfect destinations for your travel preferences...",
            ProgressDestinations);
        
        var destinationPrompt = $@"Based on the following preferences, recommend 3 travel destinations:
                                User: {travelRequest.UserName}
                                Preferences: {travelRequest.Preferences}
                                Duration: {travelRequest.DurationInDays} days
                                Budget: {travelRequest.Budget}
                                Travel Dates: {travelRequest.TravelDates}
                                Special Requirements: {travelRequest.SpecialRequirements}";

        var destinationResponse = await destinationAgent.RunAsync<DestinationRecommendations>(
            destinationPrompt,
            destinationThread);
        
        var destinationRecommendations = destinationResponse.Result;
            
        if (destinationRecommendations.Recommendations.Count == 0)
        {
            logger.LogWarning("No destination recommendations were generated");
            await StreamProgressAsync(context, travelRequest.ConversationId,
                "❌ Sorry, I couldn't find any destinations matching your preferences.\n");
            return new TravelPlanResult(CreateEmptyTravelPlan(), string.Empty);
        }
        
        // For this example, we'll take the top recommendation
        var topDestination = destinationRecommendations.Recommendations
            .OrderByDescending(r => r.MatchScore)
            .First();
            
        logger.LogInformation("Selected top destination: {DestinationName}", topDestination.DestinationName);
        
        // Stream destination selection
        await StreamProgressAsync(context, travelRequest.ConversationId,
            $"\n✅ Found your perfect destination: **{topDestination.DestinationName}** (Match: {topDestination.MatchScore}%)\n");

        // -----------------------------------------------------------------
        // Steps 2 & 3: Parallel Execution (Fan-out/Fan-in Pattern)
        // -----------------------------------------------------------------
        // Create itinerary and get local recommendations simultaneously.
        // This reduces total execution time significantly.
        // -----------------------------------------------------------------
        logger.LogInformation(
            "Steps 2 & 3: Creating itinerary and getting local recommendations for {DestinationName} in parallel", 
            topDestination.DestinationName);
        await StreamProgressAsync(context, travelRequest.ConversationId,
            $"\n📅 **Step 2/5:** Creating your day-by-day itinerary...\n\n🍽️ **Step 3/5:** Finding local restaurants & attractions...\n");
        SetOrchestrationStatus(context, "CreateItineraryAndRecommendations",
            $"Creating a detailed itinerary and finding local gems in {topDestination.DestinationName}...",
            ProgressItinerary, topDestination.DestinationName);
        
        var itineraryPrompt = $@"Create a {travelRequest.DurationInDays}-day itinerary for {topDestination.DestinationName}.
                            Dates: {travelRequest.TravelDates}
                            Budget: {travelRequest.Budget}
                            Requirements: {travelRequest.SpecialRequirements}
                            
                            CRITICAL: Keep ALL descriptions under 50 characters. Include only 2-4 activities per day.
                            Use short names and abbreviated formats.
                            
                            IMPORTANT: Determine the local currency for {topDestination.DestinationName} and use the currency converter 
                            tool to provide costs in both the user's budget currency and the destination's local currency.";
        
        var localPrompt = $@"Provide local recommendations for {topDestination.DestinationName}:
                        Duration: {travelRequest.DurationInDays} days
                        Preferred Cuisine: Any
                        Include Hidden Gems: true
                        Family Friendly: {travelRequest.SpecialRequirements.Contains("family", StringComparison.OrdinalIgnoreCase)}";
        
        // Execute both agent calls in parallel
        logger.LogInformation("Calling itinerary agent with prompt length: {Length}", itineraryPrompt.Length);
        var itineraryTask = itineraryAgent.RunAsync<TravelItinerary>(
            itineraryPrompt,
            itineraryThread);
        
        logger.LogInformation("Calling local recommendations agent");
        var localRecommendationsTask = localRecommendationsAgent.RunAsync<LocalRecommendations>(
            localPrompt,
            localThread);
        
        // Wait for both tasks to complete
        await Task.WhenAll(itineraryTask, localRecommendationsTask);
        
        var itineraryResponse = await itineraryTask;
        var localRecommendationsResponse = await localRecommendationsTask;

        // Validate and correct the cost calculation
        var itinerary = ValidateAndFixCostCalculation(itineraryResponse.Result, logger);
        var localRecommendations = localRecommendationsResponse.Result;

        // Stream itinerary completion
        await StreamProgressAsync(context, travelRequest.ConversationId,
            $"\n✅ Itinerary created: {itinerary.DailyPlan.Count} days planned, estimated cost: {itinerary.EstimatedTotalCost}\n");
        await StreamProgressAsync(context, travelRequest.ConversationId,
            $"\n✅ Found {localRecommendations.Attractions.Count} attractions and {localRecommendations.Restaurants.Count} restaurants!\n");

        // Combine all results into a comprehensive travel plan
        var travelPlan = new TravelPlan(destinationRecommendations, itinerary, localRecommendations);
        
        // -----------------------------------------------------------------
        // Step 4: Save Travel Plan to Blob Storage
        // -----------------------------------------------------------------
        logger.LogInformation("Step 4: Saving travel plan to blob storage");
        await StreamProgressAsync(context, travelRequest.ConversationId,
            "\n💾 **Step 4/5:** Saving your travel plan...\n");
        SetOrchestrationStatus(context, "SaveTravelPlan",
            "Finalizing your travel plan and preparing documentation...",
            ProgressSavingPlan, topDestination.DestinationName);
        var savePlanRequest = new SaveTravelPlanRequest(travelPlan, travelRequest.UserName);
        string? documentUrl;
        {
            documentUrl = await context.CallActivityAsync<string>(
                nameof(TravelPlannerActivities.SaveTravelPlanToBlob),
                savePlanRequest);
            
        if (string.IsNullOrEmpty(documentUrl))
        {
            logger.LogWarning("Failed to save travel plan to blob storage");
            documentUrl = null;
        }
        
        // -----------------------------------------------------------------
        // Step 5: Request User Approval (Human-in-the-Loop Pattern)
        // -----------------------------------------------------------------
        logger.LogInformation("Step 5: Requesting approval for travel plan");
        await StreamProgressAsync(context, travelRequest.ConversationId,
            "\n✅ Travel plan saved!\n\n📋 **Step 5/5:** Preparing your plan for review...\n");
        
        // Stream the complete travel plan summary
        await StreamProgressAsync(context, travelRequest.ConversationId,
            FormatTravelPlanSummary(travelPlan, context.InstanceId));
        
        SetOrchestrationStatus(context, "RequestApproval",
            "Sending travel plan for your approval...",
            ProgressRequestingApproval, topDestination.DestinationName, documentUrl);
        var approvalRequest = new ApprovalRequest(context.InstanceId, travelPlan, travelRequest.UserName);
        await context.CallActivityAsync(nameof(TravelPlannerActivities.RequestApproval), approvalRequest);
        
        // -----------------------------------------------------------------
        // Step 6: Wait for External Event (User Approval)
        // -----------------------------------------------------------------
        // The orchestration will pause here until the user approves/rejects
        // or the timeout expires (7 days by default).
        // -----------------------------------------------------------------
        logger.LogInformation(
            "Step 6: Waiting for approval from user {UserName}", 
            travelRequest.UserName);
        
        // Send completion marker to the stream - the frontend will close its connection
        // The user can later check the approval status through a separate API
        if (!string.IsNullOrEmpty(travelRequest.ConversationId))
        {
            await context.CallActivityAsync(
                nameof(TravelPlannerActivities.StreamCompletion),
                travelRequest.ConversationId);
        }
        
        // Wait for external event with timeout
        ApprovalResponse approvalResponse;
        try
        {
            SetApprovalWaitingStatus(context, topDestination.DestinationName, documentUrl, 
                itinerary, localRecommendations, logger);
            
            approvalResponse = await context.WaitForExternalEvent<ApprovalResponse>(
                "ApprovalEvent",
                TimeSpan.FromDays(ApprovalTimeoutDays));
        }
        catch (TaskCanceledException)
        {
            // If timeout occurs, use the default response
            logger.LogWarning("Approval request timed out for user {UserName}", travelRequest.UserName);
            approvalResponse = new ApprovalResponse(false, "Timed out waiting for approval");
        }
            
        // Check if the trip was approved
        if (approvalResponse.Approved)
        {
            // -----------------------------------------------------------------
            // Step 7: Book the Trip (Approved)
            // -----------------------------------------------------------------
            logger.LogInformation(
                "Step 7: Booking trip to {Destination} for user {UserName}",
                itinerary.DestinationName, 
                travelRequest.UserName);
                
            SetOrchestrationStatus(context, "BookingTrip",
                $"Booking your trip to {topDestination.DestinationName}...",
                ProgressBooking, topDestination.DestinationName, documentUrl, approved: true);
                
            var bookingRequest = new BookingRequest(travelPlan, travelRequest.UserName, approvalResponse.Comments);
            var bookingConfirmation = await context.CallActivityAsync<BookingConfirmation>(
                nameof(TravelPlannerActivities.BookTrip), bookingRequest);
                
            // Return the travel plan with booking confirmation
            logger.LogInformation("Completed travel planning for {UserName} with booking confirmation {BookingId}", 
                travelRequest.UserName, bookingConfirmation.BookingId);
            
            // Format rich booking confirmation
            var confirmationText = FormatBookingConfirmation(bookingConfirmation, travelRequest.UserName);
                
            return new TravelPlanResult(
                travelPlan, 
                documentUrl, 
                confirmationText);
        }
        else
        {
            // Return the travel plan without booking
            logger.LogInformation("Travel plan for {UserName} was not approved. Comments: {Comments}", 
                travelRequest.UserName, approvalResponse.Comments);
                
            return new TravelPlanResult(
                travelPlan, 
                documentUrl, 
                $"Travel plan was not approved. Comments: {approvalResponse.Comments}");
        }
    }
}

    // =========================================================================
    // Private Helper Methods
    // =========================================================================

    /// <summary>
    /// Creates an empty travel plan for error cases.
    /// </summary>
    private TravelPlan CreateEmptyTravelPlan()
    {
        return new TravelPlan(
            new DestinationRecommendations(new List<DestinationRecommendation>()),
            new TravelItinerary("None", "N/A", new List<ItineraryDay>(), "0", "No itinerary available"),
            new LocalRecommendations(new List<Attraction>(), new List<Restaurant>(), "No recommendations available")
        );
    }

    private TravelItinerary ValidateAndFixCostCalculation(TravelItinerary itinerary, ILogger logger)
    {
        try
        {
            // Extract all activity costs and sum them up
            decimal totalCost = 0;
            string? localCurrency = null;
            string? userCurrency = null;
            decimal exchangeRate = 1.0m;

            foreach (var day in itinerary.DailyPlan)
            {
                foreach (var activity in day.Activities)
                {
                    var cost = activity.EstimatedCost;
                    if (string.IsNullOrEmpty(cost) || 
                        cost.Equals("Free", StringComparison.OrdinalIgnoreCase) || 
                        cost.Equals("Varies", StringComparison.OrdinalIgnoreCase) ||
                        cost.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Parse cost string like "500 JPY (3.26 USD)" or "25 EUR"
                    var match = System.Text.RegularExpressions.Regex.Match(cost, @"(\d+(?:\.\d+)?)\s*([A-Z]{3})");
                    if (match.Success)
                    {
                        if (decimal.TryParse(match.Groups[1].Value, out decimal amount))
                        {
                            totalCost += amount;
                            if (localCurrency == null)
                            {
                                localCurrency = match.Groups[2].Value;
                            }
                        }

                        // Try to extract the converted currency for exchange rate calculation
                        var convertedMatch = System.Text.RegularExpressions.Regex.Match(cost, @"\((\d+(?:\.\d+)?)\s*([A-Z]{3})\)");
                        if (convertedMatch.Success && userCurrency == null)
                        {
                            userCurrency = convertedMatch.Groups[2].Value;
                            if (decimal.TryParse(convertedMatch.Groups[1].Value, out decimal convertedAmount) && amount > 0)
                            {
                                exchangeRate = convertedAmount / amount;
                            }
                        }
                    }
                }
            }

            // Calculate the corrected total cost
            var correctedLocalCost = Math.Round(totalCost, 2);
            var correctedUserCost = Math.Round(totalCost * exchangeRate, 2);

            // Format the corrected cost string
            string correctedCostString;
            if (!string.IsNullOrEmpty(userCurrency) && localCurrency != userCurrency)
            {
                correctedCostString = $"{correctedLocalCost:N0} {localCurrency} ({correctedUserCost:N2} {userCurrency})";
            }
            else
            {
                correctedCostString = $"{correctedLocalCost:N0} {localCurrency ?? "USD"}";
            }

            // Log the correction if there was a discrepancy
            if (itinerary.EstimatedTotalCost != correctedCostString)
            {
                logger.LogWarning(
                    "Cost calculation corrected. Agent calculated: {AgentCost}, Actual sum: {CorrectedCost}",
                    itinerary.EstimatedTotalCost,
                    correctedCostString);
            }

            // Return a new itinerary with the corrected cost
            return new TravelItinerary(
                itinerary.DestinationName,
                itinerary.TravelDates,
                itinerary.DailyPlan,
                correctedCostString,
                itinerary.AdditionalNotes
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating cost calculation, using agent's original value");
            return itinerary;
        }
    }

    private void SetOrchestrationStatus(
        TaskOrchestrationContext context, 
        string step, 
        string message, 
        int progress,
        string? destination = null,
        string? documentUrl = null,
        bool? approved = null)
    {
        var status = new Dictionary<string, object>
        {
            ["step"] = step,
            ["message"] = message,
            ["progress"] = progress
        };

        if (destination != null) status["destination"] = destination;
        if (documentUrl != null) status["documentUrl"] = documentUrl;
        if (approved.HasValue) status["approved"] = approved.Value;

        context.SetCustomStatus(status);
    }

    private void SetApprovalWaitingStatus(
        TaskOrchestrationContext context,
        string destinationName,
        string? documentUrl,
        TravelItinerary itinerary,
        LocalRecommendations localRecommendations,
        ILogger logger)
    {
        var waitingStatus = new {
            step = "WaitingForApproval",
            message = "Waiting for your approval of the travel plan...",
            progress = ProgressWaitingForApproval,
            destination = destinationName,
            documentUrl = documentUrl,
            travelPlan = new {
                destination = destinationName,
                dates = itinerary.TravelDates,
                cost = itinerary.EstimatedTotalCost,
                days = itinerary.DailyPlan.Count,
                dailyPlan = itinerary.DailyPlan,
                attractions = localRecommendations.Attractions.FirstOrDefault(),
                restaurants = localRecommendations.Restaurants.FirstOrDefault(),
                insiderTips = localRecommendations.InsiderTips
            }
        };

        var serialized = JsonSerializer.Serialize(waitingStatus);
        var statusSize = Encoding.Unicode.GetByteCount(serialized);
        
        if (statusSize > StatusMaxSizeBytes)
        {
            logger.LogWarning("Waiting status size ({Size} bytes) exceeds maximum ({MaxSize} bytes). Status may be truncated.", 
                statusSize, StatusMaxSizeBytes);
        }
        else
        {
            logger.LogInformation("Waiting status size: {Size} bytes", statusSize);
        }
        
        context.SetCustomStatus(waitingStatus);
    }

    /// <summary>
    /// Formats a travel plan as a markdown summary for streaming to the user.
    /// </summary>
    private static string FormatTravelPlanSummary(TravelPlan travelPlan, string instanceId)
    {
        var sb = new StringBuilder();
        var destination = travelPlan.DestinationRecommendations.Recommendations
            .OrderByDescending(r => r.MatchScore)
            .FirstOrDefault();
        var itinerary = travelPlan.Itinerary;
        var local = travelPlan.LocalRecommendations;

        sb.AppendLine("---");
        sb.AppendLine("## 🎉 Your Travel Plan is Ready!");
        sb.AppendLine();
        
        if (destination != null)
        {
            sb.AppendLine($"### 📍 Destination: {destination.DestinationName}");
            sb.AppendLine($"_{destination.Description}_");
            sb.AppendLine();
        }

        sb.AppendLine($"### 📅 Itinerary ({itinerary.DailyPlan.Count} days)");
        sb.AppendLine($"**Dates:** {itinerary.TravelDates}");
        sb.AppendLine($"**Estimated Cost:** {itinerary.EstimatedTotalCost}");
        sb.AppendLine();

        // Show all days in the itinerary
        foreach (var day in itinerary.DailyPlan)
        {
            sb.AppendLine($"**Day {day.Day}** - {day.Date}");
            foreach (var activity in day.Activities)
            {
                sb.AppendLine($"- {activity.Time}: {activity.ActivityName} ({activity.EstimatedCost})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("### 🍽️ Top Recommendations");
        
        var topAttractions = local.Attractions.Take(3).ToList();
        if (topAttractions.Count > 0)
        {
            sb.AppendLine("**Attractions:**");
            foreach (var a in topAttractions)
            {
                sb.AppendLine($"- {a.Name} ⭐{a.Rating}");
            }
        }

        var topRestaurants = local.Restaurants.Take(3).ToList();
        if (topRestaurants.Count > 0)
        {
            sb.AppendLine("**Restaurants:**");
            foreach (var r in topRestaurants)
            {
                sb.AppendLine($"- {r.Name} ({r.Cuisine}) {r.PriceRange}");
            }
        }
        sb.AppendLine();

        if (!string.IsNullOrEmpty(local.InsiderTips))
        {
            sb.AppendLine("### 💡 Insider Tip");
            sb.AppendLine($"_{local.InsiderTips}_");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("⏳ **Awaiting your approval!** Reply with 'approve' to book this trip or 'reject' to start over.");
        sb.AppendLine($"_(Orchestration ID: `{instanceId}`)_");

        return sb.ToString();
    }

    /// <summary>
    /// Formats a booking confirmation with hotel details.
    /// </summary>
    private static string FormatBookingConfirmation(BookingConfirmation confirmation, string userName)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"🎉 **Booking Confirmed!**");
        sb.AppendLine();
        sb.AppendLine($"**Confirmation Number:** `{confirmation.BookingId}`");
        sb.AppendLine($"**Traveler:** {userName}");
        sb.AppendLine($"**Booking Date:** {confirmation.BookingDate:MMMM d, yyyy}");
        sb.AppendLine();
        
        if (!string.IsNullOrEmpty(confirmation.HotelConfirmation))
        {
            sb.AppendLine($"🏨 **Hotel Confirmation:** `{confirmation.HotelConfirmation}`");
            sb.AppendLine();
        }
        
        sb.AppendLine(confirmation.ConfirmationDetails);
        
        return sb.ToString();
    }
}