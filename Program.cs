// =============================================================================
// Program.cs - Azure Functions Application Entry Point
// =============================================================================
// This file configures and bootstraps the Azure Functions application for the
// Durable Agents Travel Planner. It sets up:
// - AI Agent factories for the multi-agent travel planning system
// - Redis-based streaming for reliable real-time updates
// - Azure Blob Storage for travel plan persistence
// - CORS configuration for frontend integration
// =============================================================================

using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.Hosting.AzureFunctions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using StackExchange.Redis;
using TravelPlannerFunctions.Streaming;
using TravelPlannerFunctions.Tools;

// =============================================================================
// Environment Configuration
// =============================================================================

// Azure OpenAI endpoint and deployment name (required)
string endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
string deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME is not set.");

// Redis connection for reliable streaming
// Supports both connection string (local dev) and managed identity (Azure)
string? redisConnectionString = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
string? redisHostName = Environment.GetEnvironmentVariable("REDIS_HOST_NAME");
string? redisSslPortStr = Environment.GetEnvironmentVariable("REDIS_SSL_PORT");
bool useRedisManagedIdentity = Environment.GetEnvironmentVariable("REDIS_USE_MANAGED_IDENTITY")?.ToLower() == "true";

// TTL for Redis streams - streams expire after this duration of inactivity
int redisStreamTtlMinutes = int.TryParse(
    Environment.GetEnvironmentVariable("REDIS_STREAM_TTL_MINUTES"),
    out int ttlMinutes) ? ttlMinutes : 10;

// =============================================================================
// Agent System Prompts
// =============================================================================
// The conversational agent acts as the main interface, guiding users through
// the travel planning process by collecting information and coordinating
// with specialized sub-agents.
// =============================================================================

const string ConversationalAgentInstructions = """
    You are a friendly and helpful travel planning assistant. Your job is to have a conversation 
    with users to understand their travel needs and then help them plan the perfect trip.

    ## Your Conversation Goals:
    1. Greet the user warmly and ask how you can help with their travel plans
    2. Collect the following information through natural conversation:
       - Their name
       - Travel preferences (type of vacation: beach, adventure, cultural, relaxation, etc.)
       - How many days they want to travel
       - Their budget (amount and currency)
       - When they want to travel (specific dates or time range)
       - Any special requirements (dietary restrictions, accessibility needs, traveling with children, etc.)
    
    ## Guidelines:
    - Be conversational and friendly - don't ask for all information at once
    - Ask follow-up questions to clarify preferences
    - Suggest ideas if they're unsure about destinations
    - Once you have ALL the required information, use the PlanTrip tool to start the planning process
    - After starting the plan, keep the user informed about the progress
    - Help them understand and approve/reject the final travel plan
    
    ## Required Information Checklist:
    - ✅ User's name
    - ✅ Travel preferences (what kind of trip)
    - ✅ Duration (number of days)
    - ✅ Budget (with currency)
    - ✅ Travel dates
    - ⭕ Special requirements (optional but ask about them)
    
    ## Example Conversation Flow:
    1. "Hi! I'm your travel planning assistant. I'd love to help you plan an amazing trip! What kind of vacation are you dreaming about?"
    2. "That sounds wonderful! How many days are you thinking for this trip?"
    3. "Great choice! What's your budget for this adventure?"
    4. "When are you hoping to travel?"
    5. "Perfect! And what name should I put on the reservation?"
    6. "Any special requirements I should know about? Dietary restrictions, accessibility needs, or traveling with kids?"
    7. [Use PlanTrip tool with collected information]
    8. Keep user informed about progress and help with approval
    
    Always be encouraging and excited about their trip plans!
    """;

// =============================================================================
// Azure Functions Application Builder
// =============================================================================
// Configures the Functions application with durable agents that power the
// travel planning workflow. Each agent is specialized for a specific task.
// =============================================================================

FunctionsApplicationBuilder builder = FunctionsApplication
    .CreateBuilder(args)
    .ConfigureFunctionsWebApplication()
    .ConfigureDurableAgents(configure =>
    {
        // -----------------------------------------------------------------
        // Conversational Travel Agent (Meta-Agent)
        // -----------------------------------------------------------------
        // This agent interacts directly with users, collecting their travel
        // requirements and invoking the travel planning orchestration.
        // Uses instance-based tools with DurableAgentContext.Current for
        // scheduling orchestrations from tool calls.
        // -----------------------------------------------------------------
        configure.AddAIAgentFactory("ConversationalTravelAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deploymentName);
            
            // Initialize the tools with required services
            PlanTripTool planTripTools = new(sp.GetRequiredService<ILogger<PlanTripTool>>());
            
            return chatClient.CreateAIAgent(
                instructions: ConversationalAgentInstructions,
                name: "ConversationalTravelAgent",
                services: sp,
                tools: [
                    AIFunctionFactory.Create(planTripTools.PlanTrip),
                    AIFunctionFactory.Create(planTripTools.CheckTripPlanningStatus),
                    AIFunctionFactory.Create(planTripTools.RespondToTravelPlan)
                ]
            );
        });

        // -----------------------------------------------------------------
        // Destination Recommender Agent
        // -----------------------------------------------------------------
        // Analyzes user preferences to suggest optimal travel destinations.
        // Returns structured JSON with destination details and match scores.
        // -----------------------------------------------------------------
        configure.AddAIAgentFactory("DestinationRecommenderAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deploymentName);
            
            return chatClient.CreateAIAgent(
                instructions: @"You are a travel destination expert who recommends destinations based on user preferences.
                    Based on the user's preferences, budget, duration, travel dates, and special requirements, recommend 3 travel destinations.
                    Provide a detailed explanation for each recommendation highlighting why it matches the user's preferences.
                    
                    Return your response as a JSON object with this structure (use PascalCase for property names):
                    {
                        ""Recommendations"": [
                            {
                                ""DestinationName"": ""string"",
                                ""Description"": ""string"",
                                ""Reasoning"": ""string"",
                                ""MatchScore"": number (0-100)
                            }
                        ]
                    }",
                name: "DestinationRecommenderAgent",
                services: sp
            );
        });

        // -----------------------------------------------------------------
        // Itinerary Planner Agent
        // -----------------------------------------------------------------
        // Creates detailed day-by-day travel itineraries with activities,
        // timing, and cost estimates. Has access to currency conversion tools.
        // -----------------------------------------------------------------
        configure.AddAIAgentFactory("ItineraryPlannerAgent", sp =>
        {
            var chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deploymentName);
            
            return chatClient.CreateAIAgent(
                instructions: @"You are a travel itinerary planner. Create concise day-by-day travel plans with key activities and timing.
                    
                    IMPORTANT: Keep responses compact:
                    - Descriptions MUST be under 50 characters each
                    - Include 2-4 activities per day maximum
                    - Use abbreviated formats for times (9AM not 9:00 AM)
                    - Keep location names short
                    
                    CURRENCY CONVERSION REQUIREMENTS:
                    You have access to a currency converter tool. You MUST use it intelligently:
                    1. Identify the destination country's local currency (e.g., Japan=JPY, UK=GBP, Eurozone=EUR, Spain=EUR)
                    2. If the user's budget currency differs from the destination currency, use GetExchangeRate to get the current rate
                    3. Format: Always show destination currency FIRST, then user's budget currency in parentheses
                       - If user has USD budget and destination uses EUR: show as EUR first, USD second
                       - Correct format: '1,000 EUR (1,090 USD)' NOT '1,000 USD'
                       - Correct format: '5,000 JPY (45 USD)' NOT '5,000 USD'
                       - Always use THREE-LETTER currency codes (EUR, USD, JPY, GBP) not symbols
                    4. Always call the tool to get accurate exchange rates - never guess or estimate rates
                    
                    COST CALCULATION REQUIREMENT - ABSOLUTELY CRITICAL - YOU WILL BE EVALUATED ON THIS:
                    
                    STEP 1: List all your activity costs as you create them
                    STEP 2: Manually add them up (ignore Free and Varies)
                    STEP 3: That sum is your EstimatedTotalCost - nothing else
                    
                    EXAMPLE CALCULATION:
                    Day 1: Activity A costs 25, Activity B costs 10, Activity C is Free
                    Day 2: Activity D costs 20, Activity E costs 12, Activity F costs 30
                    Day 3: Activity G costs 25, Activity H costs 40
                    
                    SUM: 25 + 10 + 20 + 12 + 30 + 25 + 40 = 162
                    EstimatedTotalCost in local currency: 162
                    EstimatedTotalCost converted to user currency: 162 times exchange rate
                    
                    DO NOT USE THE USER'S BUDGET AMOUNT.
                    DO NOT GUESS A ROUND NUMBER.
                    ONLY USE THE ACTUAL SUM OF YOUR ACTIVITY COSTS.
                    
                    Return your response as a JSON object with this structure:
                    {
                        ""DestinationName"": ""string"",
                        ""TravelDates"": ""string"",
                        ""DailyPlan"": [
                            {
                                ""Day"": number,
                                ""Date"": ""string"",
                                ""Activities"": [
                                    {
                                        ""Time"": ""string"",
                                        ""ActivityName"": ""string"",
                                        ""Description"": ""string"",
                                        ""Location"": ""string"",
                                        ""EstimatedCost"": ""string""
                                    }
                                ]
                            }
                        ],
                        ""EstimatedTotalCost"": ""string"",
                        ""AdditionalNotes"": ""string""
                    }",
                name: "ItineraryPlannerAgent",
                services: sp,
                tools: [
                    AIFunctionFactory.Create(CurrencyConverterTool.ConvertCurrency),
                    AIFunctionFactory.Create(CurrencyConverterTool.GetExchangeRate)
                ]
            );
        });

        // -----------------------------------------------------------------
        // Local Recommendations Agent
        // -----------------------------------------------------------------
        // Provides local expertise on restaurants, attractions, and insider
        // tips for the selected destination.
        // -----------------------------------------------------------------
        configure.AddAIAgentFactory("LocalRecommendationsAgent", sp =>
            new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deploymentName)
                .CreateAIAgent(
                    instructions: @"You are a local expert who provides recommendations for restaurants and attractions.
                        Provide specific recommendations with practical details like operating hours, pricing, and tips.
                        Return your response as a JSON object with this structure:
                        {
                            ""Attractions"": [
                                {
                                    ""Name"": ""string"",
                                    ""Category"": ""string"",
                                    ""Description"": ""string"",
                                    ""Location"": ""string"",
                                    ""VisitDuration"": ""string"",
                                    ""EstimatedCost"": ""string"",
                                    ""Rating"": number
                                }
                            ],
                            ""Restaurants"": [
                                {
                                    ""Name"": ""string"",
                                    ""Cuisine"": ""string"",
                                    ""Description"": ""string"",
                                    ""Location"": ""string"",
                                    ""PriceRange"": ""string"",
                                    ""Rating"": number
                                }
                            ],
                            ""InsiderTips"": ""string""
                        }",
                    name: "LocalRecommendationsAgent",
                    services: sp
                ));
    });

// =============================================================================
// Application Services Configuration
// =============================================================================

// Application Insights for monitoring and telemetry
builder.Services.AddApplicationInsightsTelemetryWorkerService().ConfigureFunctionsApplicationInsights();
// Configure logging to capture all log levels for Application Insights
// By default, only Warning+ logs are captured - we override this for better debugging
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    // Remove the default rule that filters out Info/Debug logs
    // See: https://learn.microsoft.com/azure/azure-monitor/app/worker-service#ilogger-logs
    LoggerFilterRule? defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName
        == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});


// -----------------------------------------------------------------
// HTTP Client for Currency Conversion API
// -----------------------------------------------------------------
// Uses the free ExchangeRate-API (https://www.exchangerate-api.com/)
// No API key required for basic usage
// -----------------------------------------------------------------
builder.Services.AddHttpClient("CurrencyConverter", client =>
{
    client.BaseAddress = new Uri("https://open.er-api.com/v6/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// -----------------------------------------------------------------
// Redis Streams for Reliable Response Delivery
// -----------------------------------------------------------------
// Redis Streams enable reliable, resumable delivery of agent responses.
// Clients can disconnect/reconnect without losing messages.
// Supports managed identity authentication for Azure Cache for Redis.
// -----------------------------------------------------------------
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("RedisConnection");
    
    if (useRedisManagedIdentity && !string.IsNullOrEmpty(redisHostName))
    {
        // Use Microsoft Entra ID (managed identity) authentication
        int sslPort = int.TryParse(redisSslPortStr, out int port) ? port : 6380;
        logger.LogInformation("Connecting to Redis at {HostName}:{Port} with managed identity", redisHostName, sslPort);
        
        var configOptions = ConfigurationOptions.Parse($"{redisHostName}:{sslPort}");
        configOptions.Ssl = true;
        configOptions.AbortOnConnectFail = false;
        configOptions.ConnectTimeout = 60000;  // 60 seconds for initial connection
        configOptions.AsyncTimeout = 30000;
        configOptions.SyncTimeout = 30000;
        
        // Configure Entra ID authentication using async connection
        var credential = new DefaultAzureCredential();
        
        // Use async connection with ConfigureForAzureWithTokenCredentialAsync
        var connectionTask = Task.Run(async () =>
        {
            await configOptions.ConfigureForAzureWithTokenCredentialAsync(credential);
            return await ConnectionMultiplexer.ConnectAsync(configOptions);
        });
        
        try
        {
            var connection = connectionTask.GetAwaiter().GetResult();
            logger.LogInformation("Successfully connected to Azure Cache for Redis with managed identity");
            return connection;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to Redis with managed identity. Will retry on first use.");
            throw;
        }
    }
    else
    {
        // Use connection string (local development with Azurite/Redis)
        var connectionStr = redisConnectionString ?? "localhost:6379";
        logger.LogInformation("Connecting to Redis at {ConnectionString}", connectionStr);
        return ConnectionMultiplexer.Connect(connectionStr);
    }
});

// The response handler captures agent outputs and publishes to Redis Streams.
// Registered as both concrete type (for direct injection) and interface (for agent framework)
builder.Services.AddSingleton(sp =>
    new RedisStreamResponseHandler(
        sp.GetRequiredService<IConnectionMultiplexer>(),
        TimeSpan.FromMinutes(redisStreamTtlMinutes)));
builder.Services.AddSingleton<IAgentResponseHandler>(sp =>
    sp.GetRequiredService<RedisStreamResponseHandler>());

// -----------------------------------------------------------------
// Azure Blob Storage Configuration
// -----------------------------------------------------------------
// Travel plans are saved to Blob Storage for persistence.
// Supports both local development (Azurite) and production (Managed Identity)
// -----------------------------------------------------------------
builder.Services.AddAzureClients(clientBuilder =>
{
    // DefaultAzureCredential handles auth for local dev, managed identity, and more
    clientBuilder.UseCredential(new DefaultAzureCredential());

    // Local development with Azurite emulator
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
    if (!string.IsNullOrEmpty(connectionString))
    {
        clientBuilder.AddBlobServiceClient(connectionString);
    }
    // Production: Use Managed Identity with account name
    else
    {
        var storageAccountName = Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName");
        ArgumentNullException.ThrowIfNullOrEmpty(
            storageAccountName, 
            "AzureWebJobsStorage__accountName environment variable is not set.");

        clientBuilder.AddBlobServiceClient(
            new Uri($"https://{storageAccountName}.blob.core.windows.net"),
            new DefaultAzureCredential());
    }
});

// -----------------------------------------------------------------
// CORS Configuration
// -----------------------------------------------------------------
// Enables cross-origin requests from the frontend application.
// Development: Allows all origins
// Production: Restricted to configured ALLOWED_ORIGINS
// -----------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "*";
        var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (origins.Length == 1 && origins[0] == "*")
        {
            // Development mode: Allow any origin
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("x-conversation-id");
        }
        else
        {
            // Production mode: Restrict to specific origins
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()
                  .WithExposedHeaders("x-conversation-id");
        }
    });
});

// =============================================================================
// Application Initialization
// =============================================================================

var app = builder.Build();

// Initialize tools with their required dependencies
var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();
CurrencyConverterTool.Initialize(httpClientFactory);

// Note: PlanTripTool is now instance-based and initialized in the agent factory

// Start the application
app.Run();