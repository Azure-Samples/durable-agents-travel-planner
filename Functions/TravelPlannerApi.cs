// =============================================================================
// TravelPlannerApi.cs - HTTP API Endpoints for Direct Orchestration Control
// =============================================================================
// These API endpoints allow direct interaction with the travel planner
// orchestration, bypassing the conversational agent. Useful for:
// - Programmatic access to travel planning
// - Status monitoring and polling
// - Approval/rejection handling from external systems
// =============================================================================

using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Models;

namespace TravelPlannerFunctions.Functions;

/// <summary>
/// HTTP trigger functions for direct orchestration control.
/// These complement the conversational API for programmatic access.
/// </summary>
public class TravelPlannerApi
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes the API with logging support.
    /// </summary>
    public TravelPlannerApi(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<TravelPlannerApi>();
    }

    // =========================================================================
    // Orchestration Lifecycle Endpoints
    // =========================================================================

    /// <summary>
    /// Starts a new travel planning orchestration.
    /// POST /api/travel-planner
    /// </summary>
    /// <param name="req">HTTP request containing a TravelRequest JSON body.</param>
    /// <param name="client">Durable Task client for orchestration management.</param>
    /// <returns>202 Accepted with instance ID and status URL.</returns>
    [Function(nameof(StartTravelPlanning))]
    public async Task<HttpResponseData> StartTravelPlanning(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "travel-planner")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("Travel planning request received");

        // Parse the request
        TravelRequest travelRequest;
        try
        {
            travelRequest = await req.ReadFromJsonAsync<TravelRequest>() 
                ?? throw new InvalidOperationException("Invalid request body");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse travel request");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Invalid request format");
            return errorResponse;
        }

        // Start the orchestration
        string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(TravelPlannerOrchestrator.RunTravelPlannerOrchestration), travelRequest);

        _logger.LogInformation("Started orchestration with ID = {instanceId}", instanceId);

        // Return a response with the status URL
        var response = req.CreateResponse(HttpStatusCode.Accepted);
        
        // Add a Location header that points to the status endpoint
        response.Headers.Add("Location", $"/api/travel-planner/status/{instanceId}");
        
        await response.WriteAsJsonAsync(new
        {
            id = instanceId,
            statusQueryUrl = $"/api/travel-planner/status/{instanceId}"
        });

        return response;
    }

    /// <summary>
    /// Gets the current status of a travel planning orchestration.
    /// GET /api/travel-planner/status/{instanceId}
    /// </summary>
    /// <param name="req">HTTP request.</param>
    /// <param name="instanceId">The orchestration instance ID.</param>
    /// <param name="client">Durable Task client for status queries.</param>
    /// <returns>200 OK with orchestration status, or 404 if not found.</returns>
    [Function(nameof(GetTravelPlanningStatus))]
    public async Task<HttpResponseData> GetTravelPlanningStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "travel-planner/status/{instanceId}")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation(
            "Getting status for orchestration with ID = {instanceId}", 
            instanceId);

        // Get the orchestration status
        var status = await client.GetInstanceAsync(instanceId, true);
        _logger.LogInformation("Status for instance = {instanceId}", status); 

        if (status == null)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteStringAsync($"No orchestration found with ID = {instanceId}");
            return notFoundResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(status);
        return response;
    }

    // =========================================================================
    // Approval Handling Endpoints
    // =========================================================================

    /// <summary>
    /// Handles user approval or rejection of a travel plan.
    /// POST /api/travel-planner/approve/{instanceId}
    /// </summary>
    /// <param name="req">HTTP request containing ApprovalResponse JSON body.</param>
    /// <param name="instanceId">The orchestration instance ID awaiting approval.</param>
    /// <param name="client">Durable Task client for raising events.</param>
    /// <returns>200 OK with approval confirmation.</returns>
    [Function(nameof(HandleApprovalResponse))]
    public async Task<HttpResponseData> HandleApprovalResponse(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "travel-planner/approve/{instanceId}")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation(
            "Received approval response for orchestration with ID = {instanceId}", 
            instanceId);

        // Parse the approval response
        ApprovalResponse approvalResponse;
        try
        {
            approvalResponse = await req.ReadFromJsonAsync<ApprovalResponse>() 
                ?? throw new InvalidOperationException("Invalid approval response");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse approval response");
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Invalid approval response format");
            return errorResponse;
        }

        // Send the approval response to the orchestration
        _logger.LogInformation("Sending approval response to orchestration: Approved = {approved}", approvalResponse.Approved);
        await client.RaiseEventAsync(instanceId, "ApprovalEvent", approvalResponse);

        // Return a success response
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            message = "Approval response processed successfully",
            approved = approvalResponse.Approved
        });

        return response;
    }



    // =========================================================================
    // Confirmation Status Endpoint
    // =========================================================================

    /// <summary>
    /// Gets detailed confirmation status for a completed orchestration.
    /// GET /api/travel-planner/confirmation/{instanceId}
    /// </summary>
    /// <remarks>
    /// Returns additional fields indicating whether the booking was confirmed,
    /// rejected, or still pending.
    /// </remarks>
    /// <param name="req">HTTP request.</param>
    /// <param name="instanceId">The orchestration instance ID.</param>
    /// <param name="client">Durable Task client for status queries.</param>
    /// <returns>200 OK with confirmation details, or 404 if not found.</returns>
    [Function(nameof(GetTripConfirmationStatus))]
    public async Task<HttpResponseData> GetTripConfirmationStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "travel-planner/confirmation/{instanceId}")] HttpRequestData req,
        string instanceId,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation(
            "Getting confirmation status for orchestration with ID = {instanceId}", 
            instanceId);

        // Get the orchestration status
        var status = await client.GetInstanceAsync(instanceId, true);
        _logger.LogInformation("Confirmation status for instance = {instanceId}", status); 

        if (status == null)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteStringAsync($"No orchestration found with ID = {instanceId}");
            return notFoundResponse;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        
        // Check if the booking has been confirmed
        bool isConfirmed = false;
        bool isRejected = false;
        string confirmationMessage = "";
        
        // Check if the orchestration is completed and has output with booking confirmation
        if (status.RuntimeStatus == OrchestrationRuntimeStatus.Completed && status.ReadOutputAs<object>() != null)
        {
            try
            {
                // First try to read as JSON element
                var jsonOutput = status.ReadOutputAs<System.Text.Json.JsonElement>();
                
                // Check for booking confirmation properties
                if (jsonOutput.TryGetProperty("bookingConfirmation", out var bookingConfirmationElement) || 
                    jsonOutput.TryGetProperty("BookingConfirmation", out bookingConfirmationElement))
                {
                    if (bookingConfirmationElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        string? bookingConfirmation = bookingConfirmationElement.GetString();
                        if (bookingConfirmation != null)
                        {
                            if (bookingConfirmation.Contains("Booking confirmed"))
                            {
                                isConfirmed = true;
                                confirmationMessage = bookingConfirmation;
                            }
                            else if (bookingConfirmation.Contains("not approved"))
                            {
                                isRejected = true;
                                confirmationMessage = bookingConfirmation;
                            }
                        }
                    }
                }
                // If we couldn't find the booking confirmation property, check other properties or the whole object
                else
                {
                    string outputString = jsonOutput.ToString();
                    if (!string.IsNullOrEmpty(outputString))
                    {
                        if (outputString.Contains("Booking confirmed"))
                        {
                            isConfirmed = true;
                            confirmationMessage = outputString;
                        }
                        else if (outputString.Contains("not approved"))
                        {
                            isRejected = true;
                            confirmationMessage = outputString;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing orchestration output");
            }
        }
        
        await response.WriteAsJsonAsync(new
        {
            instanceId,
            isConfirmed,
            isRejected,
            confirmationMessage,
            status.RuntimeStatus
        });

        return response;
    }
}