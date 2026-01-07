// =============================================================================
// RedisStreamResponseHandler.cs - Reliable Streaming via Redis Streams
// =============================================================================
// This handler implements reliable delivery of AI agent responses using Redis
// Streams. Key benefits:
// - Clients can disconnect and reconnect without losing messages
// - Cursor-based resumption from any point in the stream
// - Automatic TTL-based cleanup of old streams
// - Support for both streaming and non-streaming agent responses
// =============================================================================

// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DurableTask;
using StackExchange.Redis;
using TravelPlannerFunctions.Tools;

namespace TravelPlannerFunctions.Streaming;

// =============================================================================
// Data Transfer Objects
// =============================================================================

/// <summary>
/// Represents a chunk of data read from a Redis stream.
/// </summary>
/// <param name="EntryId">The Redis stream entry ID (used as cursor for resumption).</param>
/// <param name="Text">The text content of the chunk, or null if this is a control marker.</param>
/// <param name="IsDone">True if this chunk signals the end of the stream.</param>
/// <param name="Error">An error message if something went wrong, or null otherwise.</param>
public readonly record struct StreamChunk(string EntryId, string? Text, bool IsDone, string? Error);

// =============================================================================
// Response Handler Implementation
// =============================================================================

/// <summary>
/// An implementation of <see cref="IAgentResponseHandler"/> that publishes agent
/// response updates to Redis Streams for reliable delivery.
/// </summary>
/// <remarks>
/// <para>
/// Redis Streams provide a durable, append-only log that supports consumer groups
/// and message acknowledgment. This enables clients to disconnect and reconnect
/// without losing messages.
/// </para>
/// <para>
/// Each agent session gets its own Redis Stream, keyed by conversation ID.
/// Stream entries contain text chunks with sequence numbers for ordering.
/// </para>
/// </remarks>
public sealed class RedisStreamResponseHandler : IAgentResponseHandler
{
    // =========================================================================
    // Constants
    // =========================================================================
    
    /// <summary>Maximum empty reads before timing out (5 minutes at 1s intervals).</summary>
    private const int MaxEmptyReads = 300;
    
    /// <summary>Milliseconds between poll attempts when stream is empty.</summary>
    private const int PollIntervalMs = 1000;

    // =========================================================================
    // Instance Fields
    // =========================================================================
    
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _streamTtl;

    // =========================================================================
    // Constructor
    // =========================================================================
    
    /// <summary>
    /// Initializes a new instance of the <see cref="RedisStreamResponseHandler" /> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="streamTtl">TTL for stream entries. Streams expire after this duration.</param>
    public RedisStreamResponseHandler(IConnectionMultiplexer redis, TimeSpan streamTtl)
    {
        _redis = redis;
        _streamTtl = streamTtl;
    }

    // =========================================================================
    // IAgentResponseHandler Implementation
    // =========================================================================
    
    /// <inheritdoc/>
    public async ValueTask OnStreamingResponseUpdateAsync(
        IAsyncEnumerable<AgentRunResponseUpdate> messageStream,
        CancellationToken cancellationToken)
    {
        // Get the current session ID from the DurableAgentContext
        // This is set by the AgentEntity before invoking the response handler
        DurableAgentContext? context = DurableAgentContext.Current;
        if (context is null)
        {
            throw new InvalidOperationException(
                "DurableAgentContext.Current is not set. This handler must be used within a durable agent context.");
        }

        // Get conversation ID from the current thread context
        string conversationId = context.CurrentThread.GetService<AgentThreadMetadata>()?.ConversationId
            ?? throw new InvalidOperationException("Unable to determine conversation ID from the current thread.");
        string streamKey = GetStreamKey(conversationId);

        IDatabase db = _redis.GetDatabase();
        int sequenceNumber = 0;

        await foreach (AgentRunResponseUpdate update in messageStream.WithCancellation(cancellationToken))
        {
            // Extract just the text content - this avoids serialization round-trip issues
            string text = update.Text;

            // Only publish non-empty text chunks
            if (!string.IsNullOrEmpty(text))
            {
                // Create the stream entry with the text and metadata
                NameValueEntry[] entries =
                [
                    new NameValueEntry("text", text),
                    new NameValueEntry("sequence", sequenceNumber++),
                    new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ];

                // Add to the Redis Stream with auto-generated ID (timestamp-based)
                await db.StreamAddAsync(streamKey, entries);

                // Refresh the TTL on each write to keep the stream alive during active streaming
                await db.KeyExpireAsync(streamKey, _streamTtl);
            }
        }

        // Check if an orchestration was scheduled during this agent run
        // If so, DON'T send "done" - the orchestration will handle that
        // This allows the client to keep listening for orchestration progress updates
        if (PlanTripTool.ConsumeOrchestrationScheduled(conversationId))
        {
            // Just refresh TTL but don't send done marker
            await db.KeyExpireAsync(streamKey, _streamTtl);
            return;
        }

        // No orchestration was scheduled - send done marker to close the stream
        NameValueEntry[] endEntries =
        [
            new NameValueEntry("text", ""),
            new NameValueEntry("sequence", sequenceNumber),
            new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new NameValueEntry("done", "true"),
        ];
        await db.StreamAddAsync(streamKey, endEntries);

        // Set final TTL - the stream will be cleaned up after this duration
        await db.KeyExpireAsync(streamKey, _streamTtl);
    }

    /// <inheritdoc/>
    public async ValueTask OnAgentResponseAsync(AgentRunResponse message, CancellationToken cancellationToken)
    {
        // Handle non-streaming responses (e.g., when agent uses tools)
        // We still need to write to Redis so the client can receive the response
        DurableAgentContext? context = DurableAgentContext.Current;
        if (context is null)
        {
            return; // Can't write without context
        }

        string? conversationId = context.CurrentThread.GetService<AgentThreadMetadata>()?.ConversationId;
        if (string.IsNullOrEmpty(conversationId))
        {
            return;
        }

        string streamKey = GetStreamKey(conversationId);
        IDatabase db = _redis.GetDatabase();

        // Write the full response text
        string? responseText = message.Text;
        if (!string.IsNullOrEmpty(responseText))
        {
            NameValueEntry[] entries =
            [
                new NameValueEntry("text", responseText),
                new NameValueEntry("sequence", 0),
                new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            ];
            await db.StreamAddAsync(streamKey, entries);
        }

        // Check if an orchestration was scheduled during this agent run
        // If so, DON'T send "done" - the orchestration will handle that
        if (PlanTripTool.ConsumeOrchestrationScheduled(conversationId))
        {
            // Just refresh TTL but don't send done marker
            await db.KeyExpireAsync(streamKey, _streamTtl);
            return;
        }

        // No orchestration was scheduled - send done marker to close the stream
        NameValueEntry[] endEntries =
        [
            new NameValueEntry("text", ""),
            new NameValueEntry("sequence", 1),
            new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new NameValueEntry("done", "true"),
        ];
        await db.StreamAddAsync(streamKey, endEntries);
        await db.KeyExpireAsync(streamKey, _streamTtl);
    }

    // =========================================================================
    // Public Stream Reading API
    // =========================================================================
    
    /// <summary>
    /// Reads chunks from a Redis stream, yielding them as they become available.
    /// Supports cursor-based resumption for reliable delivery.
    /// </summary>
    /// <param name="conversationId">The conversation ID to read from.</param>
    /// <param name="cursor">Optional cursor to resume from. Null reads from beginning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of stream chunks.</returns>
    public async IAsyncEnumerable<StreamChunk> ReadStreamAsync(
        string conversationId,
        string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string streamKey = GetStreamKey(conversationId);

        IDatabase db = _redis.GetDatabase();
        string startId = string.IsNullOrEmpty(cursor) ? "0-0" : cursor;

        int emptyReadCount = 0;
        bool hasSeenData = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            StreamEntry[]? entries = null;
            string? errorMessage = null;

            try
            {
                entries = await db.StreamReadAsync(streamKey, startId, count: 100);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }

            if (errorMessage != null)
            {
                yield return new StreamChunk(startId, null, false, errorMessage);
                yield break;
            }

            // entries is guaranteed to be non-null if errorMessage is null
            if (entries!.Length == 0)
            {
                if (!hasSeenData)
                {
                    emptyReadCount++;
                    if (emptyReadCount >= MaxEmptyReads)
                    {
                        yield return new StreamChunk(
                            startId,
                            null,
                            false,
                            $"Stream not found or timed out after {MaxEmptyReads * PollIntervalMs / 1000} seconds");
                        yield break;
                    }
                }

                await Task.Delay(PollIntervalMs, cancellationToken);
                continue;
            }

            hasSeenData = true;

            foreach (StreamEntry entry in entries)
            {
                startId = entry.Id.ToString();
                string? text = entry["text"];
                string? done = entry["done"];

                if (done == "true")
                {
                    yield return new StreamChunk(startId, null, true, null);
                    yield break;
                }

                if (!string.IsNullOrEmpty(text))
                {
                    yield return new StreamChunk(startId, text, false, null);
                }
            }
        }
    }

    // =========================================================================
    // Stream Writing API (for external callers)
    // =========================================================================
    
    /// <summary>
    /// Writes a message to the Redis stream for the given conversation.
    /// Used for sending messages from outside the agent context (e.g., orchestration progress).
    /// </summary>
    /// <param name="conversationId">The conversation to write to.</param>
    /// <param name="text">The message text to write.</param>
    public async Task WriteToStreamAsync(string conversationId, string text)
    {
        string streamKey = GetStreamKey(conversationId);
        IDatabase db = _redis.GetDatabase();

        NameValueEntry[] entries =
        [
            new NameValueEntry("text", text),
            new NameValueEntry("sequence", -1), // External messages have -1 sequence
            new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ];

        await db.StreamAddAsync(streamKey, entries);
        await db.KeyExpireAsync(streamKey, _streamTtl);
    }

    /// <summary>
    /// Writes a completion marker to the Redis stream.
    /// Signals to clients that no more messages will be sent.
    /// </summary>
    /// <param name="conversationId">The conversation to mark as complete.</param>
    public async Task WriteCompletionAsync(string conversationId)
    {
        string streamKey = GetStreamKey(conversationId);
        IDatabase db = _redis.GetDatabase();

        NameValueEntry[] endEntries =
        [
            new NameValueEntry("text", ""),
            new NameValueEntry("sequence", -1),
            new NameValueEntry("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            new NameValueEntry("done", "true"),
        ];

        await db.StreamAddAsync(streamKey, endEntries);
        await db.KeyExpireAsync(streamKey, _streamTtl);
    }

    /// <summary>
    /// Clears the Redis stream for a conversation.
    /// Call before starting a new agent run to ensure clients only see fresh responses.
    /// </summary>
    /// <param name="conversationId">The conversation to clear.</param>
    public async Task ClearStreamAsync(string conversationId)
    {
        string streamKey = GetStreamKey(conversationId);
        IDatabase db = _redis.GetDatabase();
        await db.KeyDeleteAsync(streamKey);
    }

    // =========================================================================
    // Internal Helpers
    // =========================================================================
    
    /// <summary>
    /// Generates the Redis Stream key for a conversation.
    /// Format: "agent-stream:{conversationId}"
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>The Redis Stream key.</returns>
    internal static string GetStreamKey(string conversationId) => $"agent-stream:{conversationId}";
}
