// =============================================================================
// TravelPlannerModels.cs - Data Transfer Objects
// =============================================================================
// This file contains all the record types used throughout the travel planning
// application. Records are used for immutability and built-in value equality.
// =============================================================================

namespace TravelPlannerFunctions.Models;

// =============================================================================
// Request Models
// =============================================================================

/// <summary>
/// Represents a user's travel planning request with all required details.
/// </summary>
/// <param name="UserName">The traveler's name for personalization and booking.</param>
/// <param name="Preferences">Desired travel style (e.g., "beach vacation", "cultural tour").</param>
/// <param name="DurationInDays">Number of days for the trip (1-30).</param>
/// <param name="Budget">Budget with currency (e.g., "$5000 USD").</param>
/// <param name="TravelDates">Preferred dates or date range.</param>
/// <param name="SpecialRequirements">Optional requirements (dietary, accessibility, etc.).</param>
/// <param name="ConversationId">Optional ID for streaming progress back to the chat interface.</param>
public record TravelRequest(
    string UserName,
    string Preferences,
    int DurationInDays,
    string Budget,
    string TravelDates,
    string SpecialRequirements,
    string? ConversationId = null
);

// =============================================================================
// Destination Models
// =============================================================================

/// <summary>
/// A single destination recommendation with match scoring.
/// </summary>
/// <param name="DestinationName">Name of the recommended destination.</param>
/// <param name="Description">Brief description of the destination.</param>
/// <param name="Reasoning">Why this destination matches the user's preferences.</param>
/// <param name="MatchScore">Score from 0-100 indicating how well it matches preferences.</param>
public record DestinationRecommendation(
    string DestinationName,
    string Description,
    string Reasoning,
    double MatchScore
);

/// <summary>
/// Collection of destination recommendations from the AI agent.
/// </summary>
/// <param name="Recommendations">List of recommended destinations.</param>
public record DestinationRecommendations(
    List<DestinationRecommendation> Recommendations
);

// =============================================================================
// Itinerary Models
// =============================================================================

/// <summary>
/// Represents a single day in the travel itinerary.
/// </summary>
/// <param name="Day">Day number (1, 2, 3, etc.).</param>
/// <param name="Date">The date for this day of travel.</param>
/// <param name="Activities">List of planned activities for the day.</param>
public record ItineraryDay(
    int Day,
    string Date,
    List<ItineraryActivity> Activities
);

/// <summary>
/// A single activity within an itinerary day.
/// </summary>
/// <param name="Time">Time of the activity (e.g., "9AM", "2PM").</param>
/// <param name="ActivityName">Name of the activity.</param>
/// <param name="Description">Brief description (kept under 50 chars for compact display).</param>
/// <param name="Location">Where the activity takes place.</param>
/// <param name="EstimatedCost">Cost in local + user currency (e.g., "500 JPY (3.50 USD)").</param>
public record ItineraryActivity(
    string Time,
    string ActivityName,
    string Description,
    string Location,
    string EstimatedCost
);

/// <summary>
/// Complete travel itinerary with daily plans and cost estimates.
/// </summary>
/// <param name="DestinationName">The destination for this itinerary.</param>
/// <param name="TravelDates">The date range for the trip.</param>
/// <param name="DailyPlan">Day-by-day breakdown of activities.</param>
/// <param name="EstimatedTotalCost">Calculated total cost (sum of all activity costs).</param>
/// <param name="AdditionalNotes">Extra notes or tips for the traveler.</param>
public record TravelItinerary(
    string DestinationName,
    string TravelDates,
    List<ItineraryDay> DailyPlan,
    string EstimatedTotalCost,
    string AdditionalNotes
);

// =============================================================================
// Local Recommendations Models
// =============================================================================

/// <summary>
/// Details about a tourist attraction.
/// </summary>
/// <param name="Name">Name of the attraction.</param>
/// <param name="Category">Type of attraction (museum, park, landmark, etc.).</param>
/// <param name="Description">Description of what to expect.</param>
/// <param name="Location">Address or area.</param>
/// <param name="VisitDuration">Recommended time to spend.</param>
/// <param name="EstimatedCost">Entry fee or typical spend.</param>
/// <param name="Rating">Rating out of 5.</param>
public record Attraction(
    string Name,
    string Category,
    string Description,
    string Location,
    string VisitDuration,
    string EstimatedCost,
    double Rating
);

/// <summary>
/// Details about a restaurant recommendation.
/// </summary>
/// <param name="Name">Restaurant name.</param>
/// <param name="Cuisine">Type of cuisine (Italian, Japanese, Local, etc.).</param>
/// <param name="Description">What makes this restaurant special.</param>
/// <param name="Location">Address or area.</param>
/// <param name="PriceRange">Price indicator ($, $$, $$$, $$$$).</param>
/// <param name="Rating">Rating out of 5.</param>
public record Restaurant(
    string Name,
    string Cuisine,
    string Description,
    string Location,
    string PriceRange,
    double Rating
);

/// <summary>
/// Collection of local recommendations including attractions, restaurants, and tips.
/// </summary>
/// <param name="Attractions">List of recommended attractions.</param>
/// <param name="Restaurants">List of recommended restaurants.</param>
/// <param name="InsiderTips">Local insider tips from the AI agent.</param>
public record LocalRecommendations(
    List<Attraction> Attractions,
    List<Restaurant> Restaurants,
    string InsiderTips
);

// =============================================================================
// Composite & Result Models
// =============================================================================

/// <summary>
/// Complete travel plan combining all agent outputs.
/// </summary>
/// <param name="DestinationRecommendations">Destination options from the recommender agent.</param>
/// <param name="Itinerary">Day-by-day itinerary from the planner agent.</param>
/// <param name="LocalRecommendations">Local tips from the recommendations agent.</param>
public record TravelPlan(
    DestinationRecommendations DestinationRecommendations,
    TravelItinerary Itinerary,
    LocalRecommendations LocalRecommendations
);

/// <summary>
/// Request to save a travel plan to blob storage.
/// </summary>
/// <param name="TravelPlan">The complete travel plan to save.</param>
/// <param name="UserName">Username for the filename.</param>
public record SaveTravelPlanRequest(
    TravelPlan TravelPlan,
    string UserName
);

/// <summary>
/// Final result of the travel planning orchestration.
/// </summary>
/// <param name="Plan">The complete travel plan.</param>
/// <param name="DocumentUrl">URL to the saved travel plan document in blob storage.</param>
/// <param name="BookingConfirmation">Booking details if the plan was approved and booked.</param>
public record TravelPlanResult(
    TravelPlan Plan,
    string? DocumentUrl,
    string? BookingConfirmation = null
);

// =============================================================================
// Approval & Booking Models
// =============================================================================

/// <summary>
/// Request for user approval of a travel plan (Human-in-the-Loop pattern).
/// </summary>
/// <param name="InstanceId">Orchestration instance ID for sending the approval response.</param>
/// <param name="TravelPlan">The plan awaiting approval.</param>
/// <param name="UserName">Name of the user who should approve.</param>
public record ApprovalRequest(
    string InstanceId,
    TravelPlan TravelPlan,
    string UserName
);

/// <summary>
/// User's response to an approval request.
/// </summary>
/// <param name="Approved">True to proceed with booking, false to reject.</param>
/// <param name="Comments">Optional feedback or comments from the user.</param>
public record ApprovalResponse(
    bool Approved,
    string Comments
);

/// <summary>
/// Request to book an approved travel plan.
/// </summary>
/// <param name="TravelPlan">The approved travel plan.</param>
/// <param name="UserName">Name of the traveler.</param>
/// <param name="ApproverComments">Any comments from the approval process.</param>
public record BookingRequest(
    TravelPlan TravelPlan,
    string UserName,
    string ApproverComments
);

/// <summary>
/// Confirmation details after successful booking.
/// </summary>
/// <param name="BookingId">Unique booking reference number.</param>
/// <param name="ConfirmationDetails">Human-readable confirmation message.</param>
/// <param name="BookingDate">When the booking was made.</param>
/// <param name="HotelConfirmation">Optional hotel-specific confirmation number.</param>
public record BookingConfirmation(
    string BookingId,
    string ConfirmationDetails,
    DateTime BookingDate,
    string? HotelConfirmation = null
);

// =============================================================================
// Utility Models
// =============================================================================

/// <summary>
/// Result of a currency conversion operation.
/// </summary>
/// <param name="FromCurrency">Source currency code (e.g., "USD").</param>
/// <param name="ToCurrency">Target currency code (e.g., "EUR").</param>
/// <param name="OriginalAmount">Amount in source currency.</param>
/// <param name="ConvertedAmount">Amount in target currency.</param>
/// <param name="ExchangeRate">The exchange rate used.</param>
/// <param name="Timestamp">When the rate was retrieved.</param>
public record CurrencyConversion(
    string FromCurrency,
    string ToCurrency,
    decimal OriginalAmount,
    decimal ConvertedAmount,
    decimal ExchangeRate,
    DateTime Timestamp
);

/// <summary>
/// Request to stream a progress update during orchestration.
/// </summary>
/// <param name="ConversationId">ID of the conversation to stream to.</param>
/// <param name="Message">The progress message to display.</param>
public record ProgressUpdateRequest(
    string? ConversationId,
    string Message
);
