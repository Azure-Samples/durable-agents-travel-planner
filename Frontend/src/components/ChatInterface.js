import React, { useState, useEffect, useRef, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import '../ChatInterface.css';

// Get API URL from environment variables
const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:7071/api';

const ChatInterface = () => {
  // Chat state - initialize from localStorage for resumability
  const [messages, setMessages] = useState(() => {
    const saved = localStorage.getItem('travel-planner-messages');
    return saved ? JSON.parse(saved) : [];
  });
  const [inputMessage, setInputMessage] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [conversationId, setConversationId] = useState(() => {
    return localStorage.getItem('travel-planner-conversation-id') || null;
  });
  const [isStreaming, setIsStreaming] = useState(false);
  const [streamingMessage, setStreamingMessage] = useState('');
  const [lastCursor, setLastCursor] = useState(() => {
    return localStorage.getItem('travel-planner-cursor') || null;
  });
  const [isResuming, setIsResuming] = useState(false);
  const [isConversationComplete, setIsConversationComplete] = useState(() => {
    return localStorage.getItem('travel-planner-complete') === 'true';
  });
  const [isTripBooked, setIsTripBooked] = useState(() => {
    return localStorage.getItem('travel-planner-booked') === 'true';
  });
  
  // Refs
  const chatHistoryRef = useRef(null);
  const eventSourceRef = useRef(null);
  const inputRef = useRef(null);

  // Auto-scroll to the bottom of chat when new messages arrive
  useEffect(() => {
    if (chatHistoryRef.current) {
      chatHistoryRef.current.scrollTop = chatHistoryRef.current.scrollHeight;
    }
  }, [messages, streamingMessage]);

  // Focus input on mount
  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  // Persist conversation ID to localStorage
  useEffect(() => {
    if (conversationId) {
      localStorage.setItem('travel-planner-conversation-id', conversationId);
    } else {
      localStorage.removeItem('travel-planner-conversation-id');
    }
  }, [conversationId]);

  // Persist messages to localStorage
  useEffect(() => {
    localStorage.setItem('travel-planner-messages', JSON.stringify(messages));
  }, [messages]);

  // Persist cursor to localStorage
  useEffect(() => {
    if (lastCursor) {
      localStorage.setItem('travel-planner-cursor', lastCursor);
    } else {
      localStorage.removeItem('travel-planner-cursor');
    }
  }, [lastCursor]);

  // Persist completion state to localStorage
  useEffect(() => {
    if (isConversationComplete) {
      localStorage.setItem('travel-planner-complete', 'true');
    } else {
      localStorage.removeItem('travel-planner-complete');
    }
  }, [isConversationComplete]);

  // Persist trip booked state to localStorage
  useEffect(() => {
    if (isTripBooked) {
      localStorage.setItem('travel-planner-booked', 'true');
    } else {
      localStorage.removeItem('travel-planner-booked');
    }
  }, [isTripBooked]);

  // Helper function to check if messages contain booking confirmation
  const checkForBookingConfirmation = (messagesArray) => {
    return messagesArray.some(msg => 
      msg.role === 'assistant' && (
        msg.content.includes('🎉 **Booking Confirmed!**') ||
        msg.content.includes('✅ **Travel Plan Approved & Booked') ||
        msg.content.includes('Your trip has been successfully booked')
      )
    );
  };

  // Auto-clear conversation on page load if trip was booked
  useEffect(() => {
    const wasBooked = localStorage.getItem('travel-planner-booked') === 'true';
    const savedMessages = localStorage.getItem('travel-planner-messages');
    
    // Check both the flag AND the actual messages for booking confirmation
    let shouldClear = wasBooked;
    
    if (!shouldClear && savedMessages) {
      try {
        const parsedMessages = JSON.parse(savedMessages);
        shouldClear = checkForBookingConfirmation(parsedMessages);
        if (shouldClear) {
          console.log('🔍 Found booking confirmation in saved messages');
        }
      } catch (e) {
        console.error('Error parsing saved messages:', e);
      }
    }
    
    if (shouldClear) {
      console.log('🎉 Trip was booked - starting fresh conversation');
      // Clear all localStorage
      localStorage.removeItem('travel-planner-conversation-id');
      localStorage.removeItem('travel-planner-messages');
      localStorage.removeItem('travel-planner-cursor');
      localStorage.removeItem('travel-planner-complete');
      localStorage.removeItem('travel-planner-booked');
      // Clear state
      setMessages([]);
      setConversationId(null);
      setLastCursor(null);
      setIsConversationComplete(false);
      setIsTripBooked(false);
    }
  }, []); // Only run once on mount

  // Auto-resume stream on page load if we have an active conversation that's NOT complete
  useEffect(() => {
    const savedConversationId = localStorage.getItem('travel-planner-conversation-id');
    const savedCursor = localStorage.getItem('travel-planner-cursor');
    const savedComplete = localStorage.getItem('travel-planner-complete') === 'true';
    
    // If conversation is complete, no need to resume streaming - data is already in localStorage
    if (savedComplete) {
      console.log('✅ Conversation already complete, restored from localStorage');
      return;
    }
    
    if (savedConversationId && savedCursor) {
      console.log(`🔄 Resuming conversation ${savedConversationId} from cursor ${savedCursor}`);
      setIsResuming(true);
      
      // Auto-resume the stream
      const autoResume = async () => {
        try {
          const url = `${API_URL}/agent/stream/${savedConversationId}?cursor=${savedCursor}`;
          const response = await fetch(url, {
            method: 'GET',
            headers: { 'Accept': 'text/event-stream' },
          });

          if (response.ok) {
            await processStream(response, false);
          }
        } catch (error) {
          console.error('Auto-resume failed:', error);
        } finally {
          setIsResuming(false);
        }
      };
      
      autoResume();
    }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // Cleanup EventSource on unmount
  useEffect(() => {
    return () => {
      if (eventSourceRef.current) {
        eventSourceRef.current.close();
      }
    };
  }, []);

  // Process SSE stream
  const processStream = useCallback(async (response, isNewConversation = false) => {
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let currentMessage = '';
    let buffer = '';

    setIsStreaming(true);
    setStreamingMessage('');

    try {
      while (true) {
        const { done, value } = await reader.read();
        
        if (done) {
          break;
        }

        buffer += decoder.decode(value, { stream: true });
        
        // Process complete SSE events from buffer
        // SSE events are separated by blank lines (\n\n)
        const events = buffer.split('\n\n');
        buffer = events.pop() || ''; // Keep incomplete event in buffer

        for (const eventBlock of events) {
          if (!eventBlock.trim()) continue;
          
          const lines = eventBlock.split('\n');
          let eventId = null;
          let eventType = 'message';
          const dataLines = [];

          for (const line of lines) {
            if (line.startsWith('id: ')) {
              eventId = line.substring(4).trim();
            } else if (line.startsWith('event: ')) {
              eventType = line.substring(7).trim();
            } else if (line.startsWith('data: ')) {
              dataLines.push(line.substring(6));
            }
          }

          if (eventId) {
            setLastCursor(eventId);
          }

          if (eventType === 'done') {
            // Stream complete - save the message
            if (currentMessage.trim()) {
              const messageContent = currentMessage.trim();
              
              // Check if this message indicates the trip was booked
              // Must match the strings in checkForBookingConfirmation()
              const isBookingConfirmation = 
                messageContent.includes('🎉 **Booking Confirmed!**') ||
                messageContent.includes('✅ **Travel Plan Approved & Booked') ||
                messageContent.includes('Your trip has been successfully booked');
              
              if (isBookingConfirmation) {
                console.log('🎉 Trip booking detected!');
                setIsTripBooked(true);
              }
              
              setMessages(prev => [...prev, { 
                role: 'assistant', 
                content: messageContent 
              }]);
            }
            setStreamingMessage('');
            setIsStreaming(false);
            setIsLoading(false);
            setIsConversationComplete(true);
            return;
          } else if (eventType === 'error') {
            console.error('Stream error:', dataLines.join('\n'));
            setIsStreaming(false);
            setIsLoading(false);
            return;
          } else if (eventType === 'message' && dataLines.length > 0) {
            // Join multi-line data with newlines
            const data = dataLines.join('\n');
            if (data !== '[DONE]') {
              currentMessage += data;
              setStreamingMessage(currentMessage);
            }
          }
        }
      }

      // Handle any remaining content
      if (currentMessage.trim() && !isStreaming) {
        setMessages(prev => [...prev, { 
          role: 'assistant', 
          content: currentMessage.trim() 
        }]);
      }
    } catch (error) {
      console.error('Stream processing error:', error);
    } finally {
      setIsStreaming(false);
      setIsLoading(false);
    }
  }, []);

  // Start a new conversation
  const startNewConversation = async (message) => {
    setIsLoading(true);
    setIsConversationComplete(false); // Reset completion state for new conversation

    try {
      const response = await fetch(`${API_URL}/agent/create`, {
        method: 'POST',
        headers: {
          'Content-Type': 'text/plain',
          'Accept': 'text/event-stream',
        },
        body: message,
      });

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
      }

      // Get conversation ID from header
      const newConversationId = response.headers.get('x-conversation-id');
      console.log('Response headers:', [...response.headers.entries()]);
      console.log('x-conversation-id header:', newConversationId);
      if (newConversationId) {
        setConversationId(newConversationId);
        console.log('Started conversation:', newConversationId);
      } else {
        console.warn('No x-conversation-id header received!');
      }

      await processStream(response, true);
    } catch (error) {
      console.error('Error starting conversation:', error);
      setMessages(prev => [...prev, { 
        role: 'assistant', 
        content: 'Sorry, I encountered an error. Please try again.' 
      }]);
      setIsLoading(false);
    }
  };

  // Continue an existing conversation
  const continueConversation = async (message) => {
    if (!conversationId) {
      // No existing conversation, start a new one
      console.log('📝 No existing conversation ID, starting new conversation');
      await startNewConversation(message);
      return;
    }

    console.log(`💬 Continuing conversation ${conversationId} with message: ${message.substring(0, 50)}...`);
    setIsLoading(true);

    try {
      const response = await fetch(`${API_URL}/agent/chat/${conversationId}`, {
        method: 'POST',
        headers: {
          'Content-Type': 'text/plain',
          'Accept': 'text/event-stream',
        },
        body: message,
      });

      console.log(`📡 Response status: ${response.status}`);

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
      }

      await processStream(response, false);
    } catch (error) {
      console.error('Error continuing conversation:', error);
      setMessages(prev => [...prev, { 
        role: 'assistant', 
        content: 'Sorry, I encountered an error. Please try again.' 
      }]);
      setIsLoading(false);
    }
  };

  // Resume stream from cursor
  const resumeStream = async () => {
    if (!conversationId) return;

    setIsLoading(true);

    try {
      const url = lastCursor 
        ? `${API_URL}/agent/stream/${conversationId}?cursor=${lastCursor}`
        : `${API_URL}/agent/stream/${conversationId}`;

      const response = await fetch(url, {
        method: 'GET',
        headers: {
          'Accept': 'text/event-stream',
        },
      });

      if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
      }

      await processStream(response, false);
    } catch (error) {
      console.error('Error resuming stream:', error);
      setIsLoading(false);
    }
  };

  // Handle form submission
  const handleSubmit = async (e) => {
    e.preventDefault();
    
    const trimmedMessage = inputMessage.trim();
    if (!trimmedMessage || isLoading) return;

    console.log('handleSubmit - current conversationId:', conversationId);

    // Add user message to chat
    setMessages(prev => [...prev, { role: 'user', content: trimmedMessage }]);
    setInputMessage('');

    // Send message to agent
    if (conversationId) {
      console.log('Continuing existing conversation:', conversationId);
      await continueConversation(trimmedMessage);
    } else {
      console.log('Starting new conversation');
      await startNewConversation(trimmedMessage);
    }
  };

  // Handle key press
  const handleKeyPress = (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSubmit(e);
    }
  };

  // Reset conversation
  const resetConversation = () => {
    // Clear localStorage
    localStorage.removeItem('travel-planner-conversation-id');
    localStorage.removeItem('travel-planner-messages');
    localStorage.removeItem('travel-planner-cursor');
    localStorage.removeItem('travel-planner-complete');
    localStorage.removeItem('travel-planner-booked');
    
    // Clear state
    setMessages([]);
    setConversationId(null);
    setStreamingMessage('');
    setIsStreaming(false);
    setIsLoading(false);
    setLastCursor(null);
    setIsResuming(false);
    setIsConversationComplete(false);
    setIsTripBooked(false);
    inputRef.current?.focus();
  };

  return (
    <div className="page-container">
      <div className="chat-title-container">
        <h1>✈️ Travel Planner Assistant</h1>
        {(conversationId || messages.length > 0) && (
          <button onClick={resetConversation} className="new-plan-btn">
            Start New Chat
          </button>
        )}
      </div>

      <div className="chat-container">
        <div ref={chatHistoryRef} className="chat-history">
          {/* Welcome message */}
          {messages.length === 0 && !isLoading && (
            <div className="welcome-message">
              <h2>👋 Welcome to the Travel Planner!</h2>
              <p>I'm here to help you plan your perfect trip. Just tell me where you'd like to go or what kind of experience you're looking for, and I'll help you create an amazing travel plan!</p>
              <div className="suggestions">
                <p><strong>Try saying:</strong></p>
                <div className="suggestion-buttons">
                  <button 
                    type="button"
                    onClick={() => {
                      setInputMessage("I want to plan a beach vacation");
                      inputRef.current?.focus();
                    }}
                  >
                    "I want to plan a beach vacation"
                  </button>
                  <button 
                    type="button"
                    onClick={() => {
                      setInputMessage("Help me plan a 7-day trip to Japan");
                      inputRef.current?.focus();
                    }}
                  >
                    "Help me plan a 7-day trip to Japan"
                  </button>
                  <button 
                    type="button"
                    onClick={() => {
                      setInputMessage("I'm looking for a family-friendly adventure");
                      inputRef.current?.focus();
                    }}
                  >
                    "I'm looking for a family-friendly adventure"
                  </button>
                </div>
              </div>
            </div>
          )}

          {/* Chat messages */}
          {messages.map((msg, index) => (
            <div key={index} className={`chat-message ${msg.role}`}>
              <div className="message-avatar">
                {msg.role === 'user' ? '👤' : '🤖'}
              </div>
              <div className="message-content">
                <ReactMarkdown>{msg.content}</ReactMarkdown>
              </div>
            </div>
          ))}

          {/* Streaming message */}
          {isStreaming && streamingMessage && (
            <div className="chat-message assistant streaming">
              <div className="message-avatar">🤖</div>
              <div className="message-content">
                <ReactMarkdown>{streamingMessage}</ReactMarkdown>
                <span className="cursor">▊</span>
              </div>
            </div>
          )}

          {/* Loading indicator */}
          {isLoading && !isStreaming && (
            <div className="chat-message assistant loading">
              <div className="message-avatar">🤖</div>
              <div className="message-content">
                <div className="typing-indicator">
                  <span></span>
                  <span></span>
                  <span></span>
                </div>
              </div>
            </div>
          )}

          {/* Resuming indicator */}
          {isResuming && (
            <div className="resume-indicator">
              🔄 Reconnecting and catching up...
            </div>
          )}
        </div>

        {/* Input area */}
        <form className="chat-input-container" onSubmit={handleSubmit}>
          <textarea
            ref={inputRef}
            className="chat-input"
            value={inputMessage}
            onChange={(e) => setInputMessage(e.target.value)}
            onKeyPress={handleKeyPress}
            placeholder="Tell me about your dream vacation..."
            disabled={isLoading}
            rows={1}
          />
          <button 
            type="submit" 
            className="send-btn"
            disabled={isLoading || !inputMessage.trim()}
          >
            {isLoading ? (
              <span className="loading-spinner">⏳</span>
            ) : (
              <span>Send ✈️</span>
            )}
          </button>
        </form>

        {/* Connection status */}
        {conversationId && (
          <div className="connection-status">
            <span className="status-dot connected"></span>
            <span className="status-text">Connected</span>
            <span className="cursor-info" title={`Cursor: ${lastCursor || 'none'}`}>
              📍 {lastCursor ? 'Resumable' : 'Starting'}
            </span>
            {lastCursor && (
              <button 
                className="resume-btn" 
                onClick={resumeStream}
                disabled={isLoading}
                title="Resume stream from last position"
              >
                🔄 Resume
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
};

export default ChatInterface;
