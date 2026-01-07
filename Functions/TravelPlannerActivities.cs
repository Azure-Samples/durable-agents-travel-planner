// =============================================================================
// TravelPlannerActivities.cs - Durable Functions Activity Functions
// =============================================================================
// Activity functions are the basic unit of work in Durable Functions.
// They handle individual tasks like saving to blob storage, sending notifications,
// and streaming progress updates. Each activity is independently retryable.
// =============================================================================

using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Models;
using TravelPlannerFunctions.Streaming;

namespace TravelPlannerFunctions.Functions;

/// <summary>
/// Contains activity functions for the travel planner orchestration.
/// Activities perform the actual work - storage operations, external calls, etc.
/// </summary>
public class TravelPlannerActivities
{
    private readonly ILogger _logger;
    private readonly BlobServiceClient _blobServiceClient;
    private readonly RedisStreamResponseHandler? _streamHandler;

    /// <summary>
    /// Initializes a new instance with required dependencies.
    /// </summary>
    /// <param name="loggerFactory">Factory for creating loggers.</param>
    /// <param name="blobServiceClient">Azure Blob Storage client.</param>
    /// <param name="streamHandler">Optional Redis stream handler for progress updates.</param>
    public TravelPlannerActivities(
        ILoggerFactory loggerFactory,
        BlobServiceClient blobServiceClient,
        RedisStreamResponseHandler? streamHandler = null)
    {
        _logger = loggerFactory.CreateLogger<TravelPlannerActivities>();
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
        _streamHandler = streamHandler;
    }

    // =========================================================================
    // Streaming Activities - Real-time Progress Updates
    // =========================================================================

    /// <summary>
    /// Streams a progress update to the conversation's Redis stream.
    /// This enables real-time UI updates as the orchestration progresses.
    /// </summary>
    /// <param name="request">Contains conversation ID and message to stream.</param>
    [Function(nameof(StreamProgressUpdate))]
    public async Task StreamProgressUpdate(
        [ActivityTrigger] ProgressUpdateRequest request)
    {
        if (string.IsNullOrEmpty(request.ConversationId) || _streamHandler == null)
        {
            _logger.LogDebug("Skipping progress update - no conversation ID or stream handler");
            return;
        }

        _logger.LogInformation(
            "Streaming progress update to conversation {ConversationId}: {Message}",
            request.ConversationId, 
            request.Message);

        await _streamHandler.WriteToStreamAsync(request.ConversationId, request.Message);
    }

    /// <summary>
    /// Marks the streaming response as complete by sending the "done" marker.
    /// Call this when the orchestration reaches a point where the client should
    /// close its connection (e.g., waiting for user approval).
    /// </summary>
    /// <param name="conversationId">The conversation to mark as complete.</param>
    [Function(nameof(StreamCompletion))]
    public async Task StreamCompletion(
        [ActivityTrigger] string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId) || _streamHandler == null)
        {
            _logger.LogDebug("Skipping stream completion - no conversation ID or stream handler");
            return;
        }

        _logger.LogInformation(
            "Sending stream completion marker to conversation {ConversationId}", 
            conversationId);

        await _streamHandler.WriteCompletionAsync(conversationId);
    }

    // =========================================================================
    // Storage Activities - Blob Storage Operations
    // =========================================================================

    /// <summary>
    /// Saves a complete travel plan to Azure Blob Storage as a formatted text file.
    /// </summary>
    /// <param name="request">Contains the travel plan and username for the filename.</param>
    /// <returns>The URL of the uploaded blob.</returns>
    [Function(nameof(SaveTravelPlanToBlob))]
    public async Task<string> SaveTravelPlanToBlob(
        [ActivityTrigger] SaveTravelPlanRequest request)
    {
        _logger.LogInformation(
            "Saving travel plan for {UserName} to blob storage", 
            request.UserName);
        
        // Create a unique filename for this travel plan
        string fileName = $"travel-plan-{request.UserName}-{DateTime.UtcNow:yyyy-MM-dd-HH-mm-ss}.txt";
        
        // Format the travel plan as text
        var content = FormatTravelPlanAsText(request.TravelPlan, request.UserName);
        
        // Get a container client using the pre-initialized BlobServiceClient
        var containerClient = _blobServiceClient.GetBlobContainerClient("travel-plans");
        await containerClient.CreateIfNotExistsAsync();
        
        // Upload the travel plan text to blob storage
        var blobClient = containerClient.GetBlobClient(fileName);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(stream, overwrite: true);
        
        _logger.LogInformation("Successfully saved travel plan to {BlobUrl}", blobClient.Uri);
        
        // Return the URL of the uploaded file
        return blobClient.Uri.ToString();
    }

    // =========================================================================
    // Workflow Activities - Approval & Booking
    // =========================================================================

    /// <summary>
    /// Requests user approval for a travel plan (Human-in-the-Loop pattern).
    /// In production, this would send an email, SMS, or push notification.
    /// </summary>
    /// <param name="request">Contains instance ID for the approval callback.</param>
    /// <returns>The approval request for tracking.</returns>
    [Function(nameof(RequestApproval))]
    public ApprovalRequest RequestApproval(
        [ActivityTrigger] ApprovalRequest request)
    {
        _logger.LogInformation(
            "Requesting approval for travel plan for user {UserName}, instance {InstanceId}",
            request.UserName, 
            request.InstanceId);
            
        // In a real app, you would send an email, SMS, or other notification
        // to the user and store the approval request in a database.
        // For demo purposes, we'll just log the request and return it.
        
        _logger.LogInformation("Approval URL: https://your-approval-app/approve?id={InstanceId}", request.InstanceId);
        
        return request;
    }

    /// <summary>
    /// Books an approved trip by integrating with booking services.
    /// In production, this would call actual hotel/flight booking APIs.
    /// </summary>
    /// <param name="request">Contains the approved travel plan and user details.</param>
    /// <returns>Booking confirmation with reference numbers.</returns>
    [Function(nameof(BookTrip))]
    public async Task<BookingConfirmation> BookTrip(
        [ActivityTrigger] BookingRequest request)
    {
        _logger.LogInformation(
            "Booking trip to {Destination} for user {UserName}",
            request.TravelPlan.Itinerary.DestinationName, 
            request.UserName);
            
        // In a real app, this would integrate with a booking system or API
        // For demo purposes, we'll simulate an async booking operation
        await Task.Delay(100); // Simulate an API call to a booking service
        
        // Generate booking IDs
        string bookingId = $"TRVL-{Guid.NewGuid().ToString()[..8].ToUpper()}";
        string hotelConfirmation = $"HTL-{Guid.NewGuid().ToString()[..6].ToUpper()}";
        
        var confirmation = new BookingConfirmation(
            BookingId: bookingId,
            ConfirmationDetails: $"Your trip to {request.TravelPlan.Itinerary.DestinationName} is confirmed for {request.UserName}. Travel dates: {request.TravelPlan.Itinerary.TravelDates}.",
            BookingDate: DateTime.UtcNow,
            HotelConfirmation: hotelConfirmation
        );
        
        _logger.LogInformation("Trip booked successfully with booking ID {BookingId}", bookingId);
        return confirmation;
    }
    
    // =========================================================================
    // Private Helper Methods
    // =========================================================================

    /// <summary>
    /// Formats a travel plan as a human-readable text document.
    /// </summary>
    /// <param name="travelPlan">The plan to format.</param>
    /// <param name="userName">Username for the document header.</param>
    /// <returns>Formatted text representation of the plan.</returns>
    private static string FormatTravelPlanAsText(TravelPlan travelPlan, string userName)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"TRAVEL PLAN FOR {userName.ToUpper()}");
        sb.AppendLine($"Generated on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine();
        
        // Add destination information
        var topDestination = travelPlan.DestinationRecommendations.Recommendations
            .OrderByDescending(r => r.MatchScore)
            .FirstOrDefault();
            
        if (topDestination != null)
        {
            sb.AppendLine("DESTINATION INFORMATION");
            sb.AppendLine("----------------------");
            sb.AppendLine($"Destination: {topDestination.DestinationName}");
            sb.AppendLine($"Match Score: {topDestination.MatchScore}");
            sb.AppendLine($"Description: {topDestination.Description}");
            sb.AppendLine();
        }
        
        // Add itinerary
        if (travelPlan.Itinerary.DailyPlan.Count > 0)
        {
            sb.AppendLine("ITINERARY");
            sb.AppendLine("---------");
            sb.AppendLine($"Destination: {travelPlan.Itinerary.DestinationName}");
            sb.AppendLine($"Travel Dates: {travelPlan.Itinerary.TravelDates}");
            sb.AppendLine($"Estimated Cost: {travelPlan.Itinerary.EstimatedTotalCost}");
            sb.AppendLine();
            
            foreach (var day in travelPlan.Itinerary.DailyPlan)
            {
                sb.AppendLine($"DAY {day.Day}: {day.Date}");
                
                // Format the activities for this day
                foreach (var activity in day.Activities)
                {
                    sb.AppendLine($"  {activity.Time}: {activity.ActivityName}");
                    sb.AppendLine($"      {activity.Description}");
                    sb.AppendLine($"      Location: {activity.Location}");
                    sb.AppendLine($"      Est. Cost: {activity.EstimatedCost}");
                    sb.AppendLine();
                }
            }
        }
        
        // Add local recommendations
        sb.AppendLine("LOCAL RECOMMENDATIONS");
        sb.AppendLine("--------------------");
        
        // Add attractions
        sb.AppendLine("Top Attractions:");
        if (travelPlan.LocalRecommendations.Attractions.Count > 0)
        {
            foreach (var attraction in travelPlan.LocalRecommendations.Attractions)
            {
                sb.AppendLine($"- {attraction.Name}: {attraction.Description}");
            }
        }
        else
        {
            sb.AppendLine("No attractions found.");
        }
        sb.AppendLine();
        
        // Add restaurants
        sb.AppendLine("Recommended Restaurants:");
        if (travelPlan.LocalRecommendations.Restaurants.Count > 0)
        {
            foreach (var restaurant in travelPlan.LocalRecommendations.Restaurants)
            {
                sb.AppendLine($"- {restaurant.Name}: {restaurant.Cuisine} cuisine, {restaurant.PriceRange}");
            }
        }
        else
        {
            sb.AppendLine("No restaurants found.");
        }
        sb.AppendLine();
        
        // Add additional notes
        if (!string.IsNullOrEmpty(travelPlan.LocalRecommendations.InsiderTips))
        {
            sb.AppendLine("Insider Tips:");
            sb.AppendLine(travelPlan.LocalRecommendations.InsiderTips);
        }
        
        return sb.ToString();
    }
}