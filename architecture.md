# Travel Planner Architecture

## System Diagram

```mermaid
%%{init: {'theme': 'base', 'themeVariables': { 'primaryColor': '#dbeafe', 'lineColor': '#64748b'}}}%%
flowchart LR
    User(["User"])

    subgraph Client["Client"]
        UI["React Chat UI"]
    end

    subgraph Backend["Azure Functions"]
        Agent["Conversational Agent"]
        
        subgraph Orch["Orchestrator"]
            direction TB
            DestAgent["Destination Agent"]
            ItinAgent["Itinerary Agent"]
            LocalAgent["Local Tips Agent"]
            Stream["Stream Progress"]
            Approve["Request Approval"]
            Book["Book Trip"]
        end
    end

    subgraph Services["External Services"]
        Redis[("Redis")]
        OpenAI["Azure OpenAI"]
        DTS[("Durable Task Scheduler")]
    end

    User -->|"interacts"| UI
    UI -->|"chat"| Agent
    Agent -->|"LLM"| OpenAI
    Agent -->|"tool"| Orch
    Agent -->|"stream"| Redis
    DestAgent -->|"LLM"| OpenAI
    ItinAgent -->|"LLM"| OpenAI
    LocalAgent -->|"LLM"| OpenAI
    Stream -->|"progress"| Redis
    Redis -->|"SSE"| UI
    Agent <-->|"state"| DTS
    Orch <-->|"state"| DTS

    style User fill:#e0f2fe,stroke:#0284c7,stroke-width:2px,color:#075985
    style Client fill:#eff6ff,stroke:#3b82f6,stroke-width:2px,color:#1e40af
    style UI fill:#93c5fd,stroke:#2563eb,stroke-width:2px,color:#1e3a8a
    style Backend fill:#fef3c7,stroke:#f59e0b,stroke-width:2px,color:#92400e
    style Agent fill:#fbbf24,stroke:#d97706,stroke-width:2px,color:#78350f
    style Orch fill:#fef9c3,stroke:#eab308,stroke-width:2px,color:#854d0e
    style DestAgent fill:#bbf7d0,stroke:#22c55e,stroke-width:2px,color:#166534
    style ItinAgent fill:#a5f3fc,stroke:#06b6d4,stroke-width:2px,color:#155e75
    style LocalAgent fill:#fbcfe8,stroke:#ec4899,stroke-width:2px,color:#9d174d
    style Stream fill:#fed7aa,stroke:#f97316,stroke-width:2px,color:#9a3412
    style Approve fill:#c7d2fe,stroke:#6366f1,stroke-width:2px,color:#3730a3
    style Book fill:#99f6e4,stroke:#14b8a6,stroke-width:2px,color:#115e59
    style Services fill:#f1f5f9,stroke:#64748b,stroke-width:2px,color:#334155
    style Redis fill:#fca5a5,stroke:#ef4444,stroke-width:2px,color:#991b1b
    style OpenAI fill:#c4b5fd,stroke:#8b5cf6,stroke-width:2px,color:#5b21b6
    style DTS fill:#a5b4fc,stroke:#6366f1,stroke-width:2px,color:#3730a3

    linkStyle default stroke:#64748b,stroke-width:2px
```

## How It Works

1. **User chats** with the React UI
2. **Conversational Agent** (Durable Entity) gathers travel details via natural conversation
3. When ready, agent **starts the Orchestrator** (Durable Function)
4. Orchestrator runs **Specialized AI Agents** (Destination, Itinerary, Local Tips)
5. Progress **streams via Redis** back to the UI in real-time
6. User **approves** the plan → booking confirmation

## Key Components

| Component | Purpose |
|-----------|---------|
| **Conversational Agent** | Collects travel info through chat |
| **Orchestrator** | Coordinates AI agents, handles approval flow |
| **Specialized AI Agents** | Generate destinations, itineraries, local tips |
| **Redis Streams** | Reliable real-time progress streaming |
| **Durable Task Scheduler** | Persists agent & orchestration state |
| **External** | Redis, Blob, DTS, OpenAI | Streaming, storage, state, AI |

## Key Flows

### 1. Chat Flow
1. User sends message → API receives → Durable Agent processes
2. Agent streams response tokens → Redis Stream → SSE → Frontend

### 2. Trip Planning Flow
1. Agent collects info → Calls `PlanTripTool` → Starts orchestration
2. Orchestration runs specialized agents in parallel
3. Progress updates stream via activities → Redis → SSE → Frontend
4. User approves → `RespondToTravelPlan` → `RaiseEventAsync` → Booking completes

### 3. Reliable Streaming
- Redis Streams provide durable, cursor-based message delivery
- Frontend can reconnect and resume from last cursor
- Orchestration progress streams through the same SSE connection