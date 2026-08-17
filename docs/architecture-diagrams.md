# Daedalus Solution Architecture Diagrams

## 1. High-Level System Architecture

This diagram shows how the different services interact in the Daedalus system:

**Key Architectural Pattern:**

- **Web Application** → REST API → Database (presentation layer)
- **Console (Ralph Loop Worker)** → Direct Database Access (background worker layer)
- **Keycloak** → OIDC Identity Provider for authentication & authorization across all layers
- Both layers share the same **Application Layer** (services, repositories) and **Infrastructure Layer** (EF Core, PostgreSQL)
- This separation allows low-latency polling and direct data manipulation for the background worker without HTTP overhead

```mermaid
graph TB
    subgraph "Client Layer"
        Web["🌐 Web Application<br/>(Blazor)"]
        Console["📟 Console<br/>(Ralph Loop Worker)"]
    end

    subgraph "API Layer"
        Api["🔌 REST API<br/>(ASP.NET Core)"]
    end

    subgraph "Orchestration"
        AppHost["☁️ .NET Aspire<br/>(Orchestrator)"]
    end

    subgraph "Infrastructure"
        DB[("🗄️ PostgreSQL<br/>(EF Core)")]
        KC["🔐 Keycloak<br/>(OIDC Identity Provider)"]
        MCP["🤖 MCP Server<br/>(Copilot Integration)"]
    end

    subgraph "Git Integration"
        GitRepo["🔀 Git Repository Manager<br/>(Clone, Branch, Commit, Push)"]
        LocalRepo["📁 Local Git Worktrees<br/>(Temp File System)"]
        RemoteRepo["☁️ Remote Git Repository<br/>(GitHub/GitLab)"]
    end

    Web -->|HTTP| Api
    Web -->|OIDC Login<br/>Token Auth| KC
    Api -->|Query/Command| DB
    Api -->|Validate Tokens<br/>JWT Bearer| KC
    Console -->|Direct Access<br/>via Repositories| DB
    Console -->|Service Account<br/>Token Auth| KC
    AppHost -->|Manages| Console
    AppHost -->|Manages| Api
    AppHost -->|Manages| DB
    AppHost -->|Manages| KC
    AppHost -->|Manages| MCP

    Console -->|Clone/Fetch/Push| GitRepo
    GitRepo -->|Create/Delete<br/>Worktrees| LocalRepo
    GitRepo -->|Commit/Push<br/>Changes| RemoteRepo
    Console -->|Repository<br/>Operations| GitRepo
```

### Data Access Architecture

**Web Application (Blazor) → API → Database:**

- User interacts with Blazor UI
- Requests routed through ASP.NET Core REST API
- API layer calls Application Services and Command/Query handlers
- Infrastructure layer handles database persistence via EF Core
- Each request is isolated within its HTTP context

**Console Application (Ralph Loop Worker) → Direct Database Access:**

- Console app doesn't go through the REST API
- Uses the same **Application Layer** (services, repositories) as the API
- Directly accesses PostgreSQL via EF Core DbContext
- Enables high-performance polling (5-second intervals) without HTTP overhead
- Uses `ITaskRepository` and `IExecutionSessionRepository` for data access
- Optimized for long-running background operations with minimal latency

**Shared Infrastructure:**
Both presentation layers (Web + Console) share:

- Common **Application Layer**: CQRS commands/queries, services, DTOs
- Common **Domain Layer**: Entities, value objects, business logic
- Common **Infrastructure Layer**: EF Core, PostgreSQL, repositories
- Service registration via dependency injection (primary constructors)

This design maximizes code reuse while allowing each presentation layer to optimize for its use case.

---

## 2. Domain Model (Entity Relationship)

Complete domain entities and their relationships, including git operations:

```mermaid
erDiagram
    PROJECT ||--o{ TASK : contains
    TASK ||--o{ TASK_EXECUTION : has
    TASK ||--o| GIT_OPERATION : contains
    EXECUTION_SESSION ||--o{ TASK : claims
    PROJECT ||--o{ EXECUTION_SESSION : tracks
    GIT_OPERATION ||--o{ GIT_DIFF : generates
    GIT_OPERATION ||--o| GIT_BRANCH : uses

    PROJECT {
        guid id PK
        string project_name
        string description
        string version
        datetime created_at
        datetime modified_at
    }

    TASK {
        guid id PK
        guid project_id FK
        string task_id
        string title
        string description
        enum priority
        string phase
        int parallel_group
        enum status
        enum complexity
        string prompt
        string completion_promise
        int max_iterations
        guid current_session_id FK
        string result
        int iteration_count
        string learnings
        string repository_url
        datetime created_at
        datetime completed_at
    }

    TASK_EXECUTION {
        guid id PK
        guid task_id FK
        int iteration_number
        string llm_response
        string output
        datetime executed_at
    }

    EXECUTION_SESSION {
        guid id PK
        string worker_name
        datetime started_at
        datetime last_heartbeat
        bool is_active
        int tasks_completed
    }

    GIT_OPERATION {
        guid id PK
        guid task_id FK
        string local_worktree_path
        string feature_branch_name
        string base_branch
        datetime started_at
        datetime completed_at
    }

    GIT_DIFF {
        guid id PK
        guid operation_id FK
        int iteration_number
        string file_path
        string original_content
        string modified_content
        datetime created_at
    }

    GIT_BRANCH {
        guid id PK
        string name
        string base_ref
        string remote_name
        string pr_url
        string commit_sha
    }
```

---

## 3. Application Layer - Command/Query Architecture

The solution uses CQRS pattern with Railway-Oriented Programming:

```mermaid
graph LR
    subgraph "Commands (Write)"
        C1["CreateTask"]
        C2["ExecuteTask"]
        C3["ConvertPrdToTasks"]
        C4["GeneratePrd"]
        C5["AbandonTask"]
    end

    subgraph "Queries (Read)"
        Q1["GetAllTasks"]
        Q2["GetTaskById"]
    end

    subgraph "Result Type<br/>Railway-Oriented"
        Success["Result.Success<T>"]
        Failure["Result.Failure<T>"]
    end

    C1 -->|Returns| Success
    C1 -->|Returns| Failure
    C2 -->|Returns| Success
    C2 -->|Returns| Failure
    C3 -->|Returns| Success
    C3 -->|Returns| Failure
    C4 -->|Returns| Success
    C4 -->|Returns| Failure
    C5 -->|Returns| Success
    C5 -->|Returns| Failure

    Q1 -->|Returns| Success
    Q1 -->|Returns| Failure
    Q2 -->|Returns| Success
    Q2 -->|Returns| Failure
```

---

## 4. Git Repository Workflow - LLM-Driven Changes

Complete workflow showing how the LLM makes changes to git repositories:

```mermaid
graph TD
    A["🟢 Task Starts<br/>Git Operations Required"] -->|Initialize| B["Clone Repository<br/>from Remote URL"]

    B -->|Download Code| C["Repository Downloaded<br/>to Temp Directory"]

    C -->|Create Isolated| D["Create Git Worktree<br/>for Feature Branch"]

    D -->|Branch Created| E["Switch to Feature Branch<br/>Ready for Changes"]

    E -->|Start Loop| F["Ralph Loop Iteration<br/>n=1..max"]

    F -->|Send Prompt| G["LLM Analyzes Codebase<br/>with File Context"]

    G -->|Generates| H["LLM Returns Changes<br/>in Patch/Diff Format"]

    H -->|Apply Changes| I["Apply Patch to<br/>Worktree Files"]

    I -->|Verify Changes| J{"Check for<br/>CompletionPromise"}

    J -->|Not Found| K["Accumulate Learnings<br/>Add File Context"]

    K -->|Enhance Prompt| L["Update Prompt with<br/>Previous Diffs & Output"]

    L -->|Loop Back| F

    J -->|Found| M["✅ Changes Complete<br/>Verification Success"]

    M -->|Stage Files| N["Git Add - Stage<br/>Modified Files"]

    N -->|Commit| O["Git Commit with<br/>Auto-Generated Message"]

    O -->|Push| P["Git Push to<br/>Feature Branch"]

    P -->|Create PR| Q["Create Pull Request<br/>on Remote Repository"]

    Q -->|Review| R["PR Ready for<br/>Code Review"]

    R -->|Cleanup| S["Delete Worktree<br/>Clean Local Temp"]

    S -->|Record Result| T["Update Task DB<br/>with PR URL & SHA"]

    T -->|End| U["🔴 Task Complete<br/>Changes Merged/Pending"]
```

---

## 4A. Git Service Architecture

Detailed breakdown of git operations and services:

```mermaid
graph TB
    subgraph "Ralph Loop Service"
        RL["RalphLoopService<br/>Orchestrates Execution"]
    end

    subgraph "Git Management Layer"
        GM["IGitRepositoryManager<br/>Interface"]

        subgraph "Repository Operations"
            GR1["Clone Repository"]
            GR2["Fetch Latest"]
            GR3["GetDiffs"]
        end

        subgraph "Branch Operations"
            GB1["Create Feature Branch"]
            GB2["Switch Branch"]
            GB3["Delete Branch"]
        end

        subgraph "Worktree Operations"
            GW1["Create Worktree"]
            GW2["Delete Worktree"]
        end

        subgraph "Change Operations"
            GC1["Apply Patch"]
            GC2["Commit Changes"]
            GC3["Push Branch"]
        end
    end

    subgraph "File System & Local Git"
        TMP["📁 Temp Directory<br/>Multiple Worktrees"]
        WC["Git Worktree<br/>Working Copy"]
        INDEX["Git Index<br/>Staging Area"]
        ODB["Git Object Database<br/>Commits, Trees, Blobs"]
    end

    subgraph "Remote Repository"
        REMOTE["☁️ GitHub/GitLab<br/>Feature Branch +<br/>Pull Request"]
    end

    RL -->|Use| GM

    GM --> GR1
    GM --> GR2
    GM --> GR3
    GM --> GB1
    GM --> GB2
    GM --> GB3
    GM --> GW1
    GM --> GW2
    GM --> GC1
    GM --> GC2
    GM --> GC3

    GR1 -->|Create| TMP
    GW1 -->|Create| WC
    GC1 -->|Modify| WC
    GC2 -->|Stage & Commit| INDEX
    INDEX -->|Persist| ODB
    GC3 -->|Upload| REMOTE

    TMP -->|Contains| WC
    WC -->|Manages| INDEX
    INDEX -->|Populates| ODB
```

---

## 4B. Code Extraction & LLM Context

How code is extracted and provided to LLM for analysis:

```mermaid
graph TB
    subgraph "Repository Context"
        RC["IRepositoryCodeExtractor<br/>Interface"]

        subgraph "Code Retrieval Methods"
            RF["Get File by Path"]
            RM["Get Multiple Files"]
            RD["Get Directory Tree"]
            RS["Search by Pattern"]
        end
    end

    subgraph "File System"
        WT["📁 Worktree<br/>Complete Code"]
        FS["File System<br/>Read Operations"]
    end

    subgraph "LLM Context Building"
        PM["Prompt Builder<br/>McpEnhancedPromptBuilder"]

        subgraph "Context Assembly"
            CF["Collect Relevant Files"]
            CT["Build Full Context<br/>with Line Numbers"]
            CL["Add Learnings from<br/>Previous Iterations"]
        end
    end

    subgraph "LLM API"
        LLM["LLM Service<br/>OpenAI/GitHub Models"]
        PROMPT["Final Prompt<br/>with Complete Context"]
    end

    RC --> RF
    RC --> RM
    RC --> RD
    RC --> RS

    RF -->|Read Files| FS
    RM -->|Read Files| FS
    RD -->|Traverse| FS
    RS -->|Search| FS

    FS -->|Source Code| WT

    PM --> CF
    PM --> CT
    PM --> CL

    CF -->|Request| RC
    CT -->|Use| RC

    CT -->|Create| PROMPT
    PROMPT -->|Send| LLM

    CL -->|Add Context| PROMPT
```

---

## 5. Ralph Loop Worker - Task Processing Pipeline

The console application implements a distributed Ralph Loop worker:

```mermaid
graph TD
    A["🟢 Worker Starts"] -->|Initialize| B["Register Session<br/>in Database"]

    B --> C{"Periodic Checks<br/>Every 5s"}

    C -->|Check| D["Poll Pending Tasks"]
    C -->|Check| E["Send Heartbeat<br/>Every 30s"]
    C -->|Check| F["Reclaim Stale Tasks<br/>Every 5m"]

    D --> G{"Task Found?"}
    G -->|No| C
    G -->|Yes| H["Claim Task<br/>Update CurrentSessionId"]

    H --> I{"Git Task?"}

    I -->|Yes| J["Clone Repository<br/>Create Worktree"]
    I -->|No| K["Start Ralph Loop<br/>without Git"]

    J -->|Repo Ready| L["Start Ralph Loop<br/>Iteration 1..n"]
    K --> L

    L --> M["Build Dynamic Prompt<br/>with File Context"]
    M --> N["Send Prompt to LLM"]
    N --> O["Get LLM Response<br/>Code Changes/Diffs"]

    O --> P["Apply Changes to<br/>Files/Worktree"]

    P --> Q{"Check for<br/>CompletionPromise"}

    Q -->|Found| R["✅ Task Complete"]
    Q -->|Not Found| S{"Max Iterations<br/>Reached?"}

    S -->|No| T["Extract Diffs<br/>Add to Learnings"]
    T --> U["Update Prompt with<br/>Execution History"]
    U --> N

    S -->|Yes| V["❌ Task Failed"]

    R -->|Git Task?| W{"Commit & Push<br/>Changes?"}
    V -->|Cleanup| W

    W -->|Yes| X["Commit Changes<br/>to Feature Branch"]
    W -->|No| X2["Keep Worktree<br/>for Review"]

    X -->|Push| Y["Push to Remote<br/>Create Pull Request"]
    X2 --> Z["Mark Task Complete"]

    Y -->|Record| Z["Save PR URL & Commit SHA<br/>Update Task Status"]

    Z --> AA["Cleanup Worktree<br/>Release Task"]

    AA --> C

    C -->|Shutdown Signal| AB["🔴 Worker Stops<br/>Mark Session Inactive"]
```

---

## 6. Project Layered Architecture

Clean architecture with clear separation of concerns:

```mermaid
graph TB
    subgraph "Presentation Layer"
        API["REST API<br/>Controllers"]
        WEB["Web UI<br/>Blazor Components"]
        CONSOLE["Console App<br/>Ralph Loop Worker"]
    end

    subgraph "Application Layer"
        COMMANDS["Command Handlers<br/>(Write Operations)"]
        QUERIES["Query Handlers<br/>(Read Operations)"]
        SERVICES["Application Services"]
        DTOs["Data Transfer Objects"]
    end

    subgraph "Domain Layer"
        ENTITIES["Domain Entities<br/>Project, Task, etc."]
        VALUEOBJECTS["Value Objects<br/>Priority, Status, etc."]
        AGGROOT["Aggregate Roots"]
    end

    subgraph "Infrastructure Layer"
        DBCONTEXT["EF Core DbContext"]
        REPOSITORIES["Repositories"]
        MIGRATION["Database Migrations"]
        PERSISTENCE["PostgreSQL"]
    end

    API --> COMMANDS
    API --> QUERIES
    WEB --> SERVICES
    CONSOLE --> SERVICES

    COMMANDS --> SERVICES
    QUERIES --> SERVICES

    SERVICES --> REPOSITORIES
    SERVICES --> ENTITIES

    REPOSITORIES --> DBCONTEXT
    ENTITIES --> VALUEOBJECTS
    ENTITIES --> AGGROOT

    DBCONTEXT --> PERSISTENCE
    MIGRATION --> PERSISTENCE
```

---

## 7. Complete Data Flow - Git-Based Task Execution

How data flows from creation through git operations to completion:

```mermaid
graph LR
    subgraph "Task Creation"
        A["User Creates Task<br/>Specifies Repo URL"]
        B["Task Entity<br/>Created with Git Repo"]
        C["Task Persisted<br/>Status: Pending"]
    end

    subgraph "Repository Initialization"
        D["Worker Polls<br/>Finds Task"]
        E["Clone Repository<br/>from Remote URL"]
        F["Create Feature Branch<br/>& Worktree"]
    end

    subgraph "First Iteration"
        G["Extract Code Files<br/>Build Context"]
        H["Send Full Codebase<br/>Context to LLM"]
        I["LLM Analyzes<br/>Returns Changes"]
    end

    subgraph "Change Application Loop"
        J["Apply Patch/Changes<br/>to Worktree"]
        K["Check for<br/>CompletionPromise"]
        L["Diff Generated<br/>from Changes"]
        M["Add Diff to<br/>Learnings Context"]
    end

    subgraph "Completion"
        N["✅ CompletionPromise<br/>Found"]
        O["Stage & Commit<br/>Changes in Git"]
        P["Push to Feature Branch<br/>Create PR"]
    end

    subgraph "Finalization"
        Q["Save PR URL &<br/>Commit SHA"]
        R["Mark Task Complete<br/>Set CompletedAt"]
        S["Delete Worktree<br/>Release Task"]
    end

    A --> B --> C --> D
    D --> E --> F
    F --> G --> H --> I
    I --> J --> K
    K -->|No Match| L --> M --> H
    K -->|Match| N --> O --> P
    P --> Q --> R --> S
```

---

## 7A. Data Flow - Task Execution

How data flows from creation to completion using direct database access:

```mermaid
graph LR
    subgraph "Task Creation"
        A["User/API<br/>Creates Task"]
        B["Task Entity<br/>Created"]
        C["Task Persisted<br/>Status: Pending"]
    end

    subgraph "Task Claiming (Direct DB Access)"
        D["Ralph Loop Worker<br/>Polls DB"]
        E["Worker Claims Task<br/>Sets CurrentSessionId"]
        F["Task Status Updates<br/>In Progress"]
    end

    subgraph "Task Execution"
        G["Send Prompt to<br/>GitHub Copilot LLM"]
        H["LLM Processes<br/>Analyzes Code"]
        I["LLM Returns<br/>Response"]
        J["Check for<br/>CompletionPromise"]
    end

    subgraph "Learning Loop"
        K["Aggregate Learnings<br/>from Previous Runs"]
        L["Enhance Prompt<br/>Add Context"]
        M["Increment Iteration<br/>Counter"]
    end

    subgraph "Task Completion"
        N["✅ CompletionPromise<br/>Found"]
        O["Save Final Result<br/>LLM Output"]
        P["Mark Task Complete<br/>Set CompletedAt"]
        Q["Release Task"]
    end

    A --> B --> C --> D
    D --> E --> F --> G
    G --> H --> I --> J
    J -->|No Match| K --> L --> M --> G
    J -->|Match Found| N --> O --> P --> Q
```

---

## 8. Multi-Instance Worker Coordination

How multiple workers coordinate on the same task queue:

```mermaid
graph TB
    subgraph "Database"
        TASKS["Task Queue<br/>Status: Pending"]
        SESSIONS["Active Sessions<br/>Worker Heartbeats"]
    end

    subgraph "Worker Instances"
        W1["Worker 1<br/>machine-001"]
        W2["Worker 2<br/>machine-002"]
        W3["Worker 3<br/>machine-003"]
    end

    subgraph "Coordination"
        LOCK["Optimistic Locking<br/>CurrentSessionId"]
        HB["Heartbeat Monitoring<br/>5min Stale Timeout"]
        RC["Reclaim Stale Tasks<br/>Auto-Reset"]
    end

    W1 -->|Poll Every 5s| TASKS
    W2 -->|Poll Every 5s| TASKS
    W3 -->|Poll Every 5s| TASKS

    W1 -->|Claim Task| LOCK
    W2 -->|Claim Task| LOCK
    W3 -->|Claim Task| LOCK

    LOCK -->|Prevents Duplicates| TASKS

    W1 -->|Heartbeat Every 30s| SESSIONS
    W2 -->|Heartbeat Every 30s| SESSIONS
    W3 -->|Heartbeat Every 30s| SESSIONS

    HB -->|Monitor| SESSIONS
    RC -->|Release If Stale| TASKS
```

---

## 9. Dependency Injection & Service Resolution

How services are wired together:

```mermaid
graph TB
    subgraph "Service Registration<br/>Program.cs"
        REG1["AddApplicationDatabase"]
        REG2["AddScoped: Repositories"]
        REG3["AddScoped: Query Services"]
        REG4["AddScoped: Command Handlers"]
        REG5["AddSingleton: Configuration"]
        REG6["AddHostedService: RalphLoopWorker"]
        REG7["AddAuthentication<br/>+ AddJwtBearer (Keycloak)"]
        REG8["AddAuthorization<br/>Policies & Roles"]
    end

    subgraph "Service Container"
        DI["IServiceProvider<br/>Dependency Injection"]
    end

    subgraph "Service Lifecycle"
        SINGLETON["Singleton<br/>One per app lifetime"]
        SCOPED["Scoped<br/>One per HTTP request"]
        TRANSIENT["Transient<br/>New each time"]
    end

    subgraph "Resolved Services"
        SERVICES["TaskQueryService<br/>TaskRepository<br/>DbContext<br/>ILogger<br/>Configuration"]
    end

    subgraph "Auth Services"
        AUTH["IAuthenticationService<br/>IAuthorizationService<br/>JwtBearerHandler<br/>Keycloak OIDC"]
    end

    REG1 --> DI
    REG2 --> DI
    REG3 --> DI
    REG4 --> DI
    REG5 --> DI
    REG6 --> DI
    REG7 --> DI
    REG8 --> DI

    DI --> SINGLETON
    DI --> SCOPED
    DI --> TRANSIENT

    SINGLETON --> SERVICES
    SCOPED --> SERVICES
    TRANSIENT --> SERVICES
    SCOPED --> AUTH
```

---

## 10. API Controllers & Endpoints

REST API surface and routing:

```mermaid
graph TB
    subgraph "API Endpoints"
        A["📌 DataControllers<br/>/api/data/*"]
        B["📌 CodeAnalysisController<br/>/api/codeanalysis/*"]
        C["📌 RepositoriesController<br/>/api/repositories/*"]
        D["📌 PrdController<br/>/api/prd/*"]
    end

    subgraph "Controllers Implementation"
        A1["Tasks CRUD<br/>GET/POST/PUT/DELETE"]
        A2["Projects CRUD<br/>GET/POST/PUT/DELETE"]
        B1["Code Quality Analysis<br/>Violations, Metrics"]
        C1["Repository Info<br/>Files, Structure"]
        D1["PRD Generation<br/>Auto-generate Specs"]
    end

    subgraph "Response Format"
        R1["200 OK<br/>With DTO Payload"]
        R2["404 Not Found<br/>Error Message"]
        R3["400 Bad Request<br/>Validation Error"]
        R4["500 Internal Error<br/>Failure Message"]
    end

    A --> A1
    A --> A2
    B --> B1
    C --> C1
    D --> D1

    A1 --> R1
    A2 --> R1
    B1 --> R1
    C1 --> R1
    D1 --> R1

    A1 -.->|Validation| R3
    A1 -.->|Not Found| R2
    A1 -.->|Exception| R4
```

---

## 11. Technology Stack & Integration Points

Complete tech stack overview:

```mermaid
graph TB
    subgraph "Frontend"
        BLAZOR["Blazor WebAssembly<br/>Interactive UI"]
        JS["JavaScript<br/>Browser Runtime"]
    end

    subgraph "Backend Services"
        API["ASP.NET Core 10<br/>REST API"]
        WORKER["Console App<br/>Background Worker"]
        ASPIRE["🚀 .NET Aspire<br/>Orchestration"]
    end

    subgraph "Identity & Security"
        KEYCLOAK["🔐 Keycloak 26<br/>OIDC Identity Provider"]
        JWT["JWT Bearer<br/>Token Validation"]
        OIDC["OpenID Connect<br/>Authorization Code Flow"]
    end

    subgraph "Language & Framework"
        CSharp["C# 13.0<br/>Latest Language Features"]
        DotNET["🟦 .NET 10<br/>High Performance"]
    end

    subgraph "Patterns & Libraries"
        ROP["Railway-Oriented<br/>Programming"]
        CFE["CSharpFunctional<br/>Extensions"]
        ZLINQ["ZLinq<br/>Zero-Allocation LINQ"]
    end

    subgraph "Data & Persistence"
        EFC["Entity Framework<br/>Core 10"]
        NPGSQL["Npgsql<br/>PostgreSQL Driver"]
        POSTGRES["🐘 PostgreSQL 16<br/>Relational Database"]
    end

    subgraph "Git & File Operations"
        GRM["GitRepositoryManager<br/>Clone, Branch, Commit"]
        RCE["RepositoryCodeExtractor<br/>File Reading"]
        FS["System.IO<br/>File System Operations"]
        GIT["🔀 Git CLI/API<br/>Version Control"]
    end

    subgraph "Testing"
        XUNIT["xUnit<br/>Unit Testing"]
        CONTAINERS["TestContainers<br/>Integration Tests"]
        PLAYWRIGHT["Playwright<br/>E2E Testing"]
    end

    subgraph "Code Quality"
        SONAR["SonarAnalyzer<br/>Code Quality"]
        MEZIANTOU["Meziantou.Analyzer<br/>Performance Analysis"]
        NETANALYZERS["NetAnalyzers<br/>Best Practices"]
    end

    subgraph "Observability"
        OTEL["OpenTelemetry<br/>Distributed Tracing"]
        LOGGING["ILogger<br/>Structured Logging"]
    end

    BLAZOR --> API
    BLAZOR -->|OIDC Login| KEYCLOAK
    API --> ROP
    API -->|Validate Tokens| JWT
    WORKER --> ROP
    WORKER --> EFC
    WORKER -->|Service Account| KEYCLOAK
    API --> EFC

    KEYCLOAK --> OIDC
    KEYCLOAK --> JWT
    KEYCLOAK --> POSTGRES

    ROP --> CFE
    API --> ZLINQ

    EFC --> NPGSQL
    NPGSQL --> POSTGRES

    ASPIRE -->|Coordinates| API
    ASPIRE -->|Coordinates| WORKER
    ASPIRE -->|Coordinates| POSTGRES
    ASPIRE -->|Coordinates| KEYCLOAK

    WORKER -->|Clone/Commit/Push| GRM
    GRM -->|Manage| RCE
    RCE -->|Read Files| FS
    FS -->|Execute| GIT

    CSharp --> DotNET
    CFE --> DotNET

    API --> XUNIT
    WORKER --> CONTAINERS
    BLAZOR --> PLAYWRIGHT

    API --> SONAR
    API --> MEZIANTOU
    API --> NETANALYZERS

    API --> OTEL
    API --> LOGGING
    WORKER --> LOGGING
    GRM --> LOGGING
```

---

## 12. Request-Response Lifecycle

Complete request flow from client to database and back:

```mermaid
sequenceDiagram
    participant Client as Client<br/>Web/API
    participant Auth as Auth Middleware<br/>JWT Bearer
    participant KC as Keycloak<br/>OIDC Provider
    participant Controller as Controller
    participant Handler as Command/Query<br/>Handler
    participant Service as Application<br/>Service
    participant Repo as Repository
    participant EF as EF Core<br/>DbContext
    participant DB as PostgreSQL

    Client->>Auth: HTTP Request<br/>+ Bearer Token
    activate Auth

    Auth->>KC: Validate JWT Token<br/>Check Signature & Claims
    activate KC
    KC-->>Auth: Token Valid<br/>User Claims
    deactivate KC

    alt Token Invalid or Expired
        Auth-->>Client: 401 Unauthorized
    end

    Auth->>Controller: Authenticated Request<br/>ClaimsPrincipal Set
    deactivate Auth
    activate Controller

    Controller->>Handler: Instantiate Handler<br/>DI Resolution
    activate Handler

    Handler->>Service: Call Application<br/>Service Method
    activate Service

    Service->>Repo: Query/Execute via<br/>Repository
    activate Repo

    Repo->>EF: DbSet Query<br/>AsNoTracking
    activate EF

    EF->>DB: Execute SQL
    activate DB

    DB-->>EF: Result Set
    deactivate DB

    EF-->>Repo: Materialized Entities
    deactivate EF

    Repo-->>Service: IReadOnlyList<T>
    deactivate Repo

    Service->>Service: Business Logic<br/>Map to DTO

    Service-->>Handler: Result.Success(DTO)
    deactivate Service

    Handler-->>Controller: Result Object<br/>Success or Failure
    deactivate Handler

    Controller->>Controller: Match Result<br/>Map to Response

    Controller-->>Client: 200 OK / 4xx Error<br/>JSON Response
    deactivate Controller
```

---

## 13. Performance Optimization Strategy

Key performance optimizations across the stack:

```mermaid
graph TB
    subgraph "Memory Optimization"
        M1["Use Span&lt;T&gt;<br/>Stack Buffers"]
        M2["ArrayPool&lt;T&gt;<br/>Buffer Reuse"]
        M3["ZLinq<br/>Zero-Allocation LINQ"]
        M4["ValueTask&lt;T&gt;<br/>Sync Completion Opt"]
        M5["readonly struct<br/>Avoid Copies"]
    end

    subgraph "Database Optimization"
        D1["AsNoTracking()<br/>Read-Only Queries"]
        D2["DbContext Pooling<br/>Connection Reuse"]
        D3["Compiled Queries<br/>Frequent Operations"]
        D4["Split Queries<br/>Avoid Cartesian"]
        D5["ExecuteUpdateAsync<br/>Bulk Operations"]
    end

    subgraph "API Optimization"
        A1["Response Compression<br/>Gzip + Fastest Level"]
        A2["JSON Source Gen<br/>Compile-Time Serialization"]
        A3["Response Caching<br/>IMemoryCache"]
        A4["LoggerMessageAttribute<br/>Compile-Time Logging"]
    end

    subgraph "Code Quality & Safety"
        C1["SonarAnalyzer<br/>Static Analysis"]
        C2["Meziantou Analyzer<br/>Performance Checks"]
        C3["NetAnalyzers<br/>Best Practice Rules"]
        C4["Railway-Oriented<br/>Error Handling"]
    end

    style M1 fill:#e1f5ff
    style M2 fill:#e1f5ff
    style M3 fill:#e1f5ff
    style M4 fill:#e1f5ff
    style M5 fill:#e1f5ff
    style D1 fill:#f3e5f5
    style D2 fill:#f3e5f5
    style D3 fill:#f3e5f5
    style D4 fill:#f3e5f5
    style D5 fill:#f3e5f5
    style A1 fill:#e8f5e9
    style A2 fill:#e8f5e9
    style A3 fill:#e8f5e9
    style A4 fill:#e8f5e9
    style C1 fill:#fff3e0
    style C2 fill:#fff3e0
    style C3 fill:#fff3e0
    style C4 fill:#fff3e0
```

---

## 14. Agent turn (Thalos)

Phase 1.1 adds a second, general-purpose agent stack next to the Ralph Loop: **Thalos.NET** (Microsoft Agent Framework
1.17 underneath, AI.Sentinel at the model boundary, MCP + local tools with authorization at the function boundary). The
Blazor page `/agent` talks to `Daedalus.Api` over REST, and one turn is streamed back as Server-Sent Events. Sessions and
transcripts live in PostgreSQL via `PostgresAgentSessionStore` (`Daedalus.Agents`).

Phase 1.2 adds **memory** to the turn: before the model is called, Thalos' memory context provider recalls the memories
visible to the caller and injects them into the prompt; `memory__*` tools let the agent remember/recall/forget
explicitly. Records live in `AgentMemories` (`PostgresMemoryStore`), embeddings in the Rag.NET index (`rag_chunks`, same
database) — a rebuildable cache, so an unavailable index degrades to `index_pending` rows instead of a failed turn.

```mermaid
sequenceDiagram
    autonumber
    participant Web as Daedalus.Web<br/>(Agent.razor + AgentApiClient)
    participant Api as Daedalus.Api<br/>AgentSessionsController<br/>AgentMemoriesController
    participant RT as Thalos IAgentRuntime<br/>(ThalosAgentRuntime)
    participant Store as PostgresAgentSessionStore<br/>(Daedalus.Agents → EF Core)
    participant Mem as MemoryContextProvider<br/>→ IMemoryService
    participant MemStore as PostgresMemoryStore<br/>(AgentMemories) + Rag.NET index<br/>(rag_chunks, nomic-embed-text 768)
    participant Agent as MAF ChatClientAgent
    participant Sentinel as AI.Sentinel<br/>(SentinelChatClient decorator)
    participant Claude as Anthropic API<br/>(Thalos.NET.Anthropic)
    participant Tools as Tools<br/>(MCP roslyn/context7, local daedalus__*, memory__*)

    Web->>Api: POST /api/agents/sessions/{id}/turns/stream<br/>Authorization: Bearer (policy AgentUse)
    Api->>Api: 200 text/event-stream, DisableBuffering,<br/>": connected" flushed before the first token
    Api->>RT: RunTurnStreamingAsync(AgentTurnRequest{session, text, caller})
    RT->>Store: Load session (owner/admin check, else SessionNotFound)<br/>Idle → Running
    Store-->>RT: session + transcript
    RT->>Mem: Recall for this turn<br/>(scope: caller + this agent's pinned + shared owner "daedalus")
    Mem->>MemStore: embed(prompt) → vector search in rag_chunks<br/>→ hydrate records from AgentMemories
    alt Recall succeeded (0..TopK hits above MinScore)
        MemStore-->>Mem: RecalledMemory[] (TopK, MaxChars budget)
        Mem->>Mem: Untrusted-content scan;<br/>quarantined memories dropped from the block
        Mem-->>RT: memory block prepended to the prompt
        RT-->>Api: memory-recalled (ids, chars)<br/>[memory-quarantined per dropped memory]
        Api-->>Web: event: memory-recalled / memory-quarantined
    else Index unavailable or failed
        MemStore-->>Mem: MemoryIndexUnavailable / MemoryIndexFailed
        RT-->>Api: memory-recall-failed (code)
        Api-->>Web: event: memory-recall-failed<br/>(turn continues without memories)
    end
    RT->>Agent: Build from AgentDefinition<br/>(instructions, model, tool glob, history,<br/>recalled memories)
    loop Model ↔ tools until the final answer
        Agent->>Sentinel: chat request (streaming)
        Sentinel->>Sentinel: input detectors (lexical; semantic when an<br/>embedding generator is configured)
        alt Critical finding → Quarantine
            Sentinel-->>RT: SentinelException → AgentError Quarantined
            RT->>Store: Running → Idle
            RT-->>Api: error (Quarantined)
            Api-->>Web: event: error
        else Clean / Log / Alert
            Sentinel->>Claude: messages + tool schemas
            Claude-->>Sentinel: text deltas / tool_use blocks
            Sentinel->>Sentinel: output detectors
            Sentinel-->>Agent: updates
            Agent-->>RT: text-delta
            RT-->>Api: text-delta
            Api-->>Web: event: text-delta (flushed immediately)
            opt Model calls a tool
                Agent->>Tools: AuthorizingAIFunction: policy check at the function boundary<br/>(roslyn__apply_* → developer/admin role)
                alt Denied
                    Tools-->>Agent: "Tool call denied: …" as tool result<br/>(ToolCallDeniedNotification published)
                else Allowed
                    Tools->>Tools: MCP call (stdio/http) or local method
                    Tools-->>Agent: tool result
                end
                RT-->>Api: tool-call / tool-result
                Api-->>Web: event: tool-call / tool-result
                opt memory__remember / memory__forget
                    Tools->>Mem: IMemoryService.RememberAsync (dedupe check)
                    Mem->>MemStore: insert AgentMemories row + upsert rag_chunks
                    alt Indexed
                        RT-->>Api: memory-stored (id, kind, deduped)
                    else No embedding generator / index down
                        RT-->>Api: memory-stored + memory-index-pending (id)<br/>ReindexPendingMemoriesHostedService retries later
                    end
                    Api-->>Web: event: memory-stored / memory-index-pending
                end
            end
        end
    end
    RT->>Store: Append user + assistant turn, usage,<br/>Running → Idle
    RT-->>Api: usage, done
    Api-->>Web: event: usage, event: done
    Web->>Web: Render bubbles, tool cards, usage;<br/>re-enable composer
    Web->>Api: GET /api/agent-memories/{id}<br/>(AgentMemoriesController: hydrate the recalled ids<br/>for the Memories panel)
    Api-->>Web: MemoryDto per visible id
```

Notes:

- The controller flushes every SSE frame as soon as the runtime yields it (`text-delta`, `tool-call`, `tool-result`,
  `usage`, `done`, `error`, `memory-recalled`, `memory-stored`, `memory-recall-failed`, `memory-index-pending`,
  `memory-quarantined`); the stream always ends with `done` or `error`.
- **Memory is best-effort within the turn:** a failed or unavailable index yields `memory-recall-failed` and the turn
  runs without memories; a write that could not be embedded yields `memory-index-pending` and is repaired in the
  background by `ReindexPendingMemoriesHostedService` (API host only). `AgentMemories` is the source of truth,
  `rag_chunks` is a rebuildable cache — but the sweeper runs with `PendingOnly = true`, so a full rebuild means dropping
  `rag_chunks` **and** setting `IndexPending` on the rows (see the README's operational notes).
- The `memory-recalled` event carries **ids only**, so the Blazor Memories panel hydrates them through
  `GET /api/agent-memories/{id}`, which applies the same visibility rule as the tools (`MemoryScope.Includes`).
- Tool authorization runs at the **function boundary** (`Thalos` `AuthorizingAIFunction` + `[Policy]` types such as
  `DeveloperPolicy`), so an unauthorized tool call is reported back to the model instead of failing the turn; the denial
  is also published as a `ToolCallDeniedNotification` (audit).
- AI.Sentinel is an `IChatClient` decorator: a `Quarantine` verdict surfaces as `AgentErrorCode.Quarantined` (HTTP 422 on
  the buffered endpoint, `event: error` on the stream). Its semantic detectors need the Ollama embedding generator; without
  it only lexical/operational detectors run.
- On host start `AgentSessionCrashRecovery` resets any session left in `Running` by a crashed process back to `Idle`.

### Strangler layout: Ralph and Thalos side by side

Until phase 1.6 both stacks are wired in the API and share Infrastructure (DbContext) — and, since phase 1.2, the agent
memory: Ralph's learnings are written and recalled through the Application port `ILearningsMemory` (shared owner
`daedalus`), so `StructuredLearnings` and the hand-rolled embedding service are gone. The Ralph worker runs in
`Daedalus.Console`, which registers `AddDaedalusMemory` (memory only, no agents, creates no Rag.NET schema):

```mermaid
graph LR
    subgraph "Daedalus.Web (Blazor WASM)"
        Pages["Tasks / Sessions / Executions /<br/>Ralph Config / PRD Generator"]
        AgentPage["/agent<br/>(Agent.razor)"]
    end

    subgraph "Daedalus.Api"
        RalphCtrl["Ralph controllers<br/>(tasks, sessions, ralph-config, …)"]
        AgentCtrl["AgentsController<br/>AgentSessionsController (SSE)<br/>AgentMemoriesController"]
    end

    subgraph "Ralph stack (legacy, retired in 1.6)"
        Ralph["RalphLoopOrchestrator<br/>Daedalus.Console worker"]
        RalphLlm["ILlmService<br/>Copilot / Claude"]
        LearnPort["ILearningsMemory (Application port)<br/>LearningsService · enrichment middleware<br/>search_learnings MCP tool"]
        ConsoleComposition["AddDaedalusMemory<br/>(memory only, no schema)"]
    end

    subgraph "Thalos stack (Daedalus.Agents)"
        Composition["AddDaedalusAgents<br/>(Thalos:* config, .mcp.json,<br/>owns the Rag.NET schema)"]
        Runtime["Thalos IAgentRuntime"]
        SessionStore["PostgresAgentSessionStore"]
        Knowledge["DaedalusKnowledgeTools<br/>(daedalus__search_failure_patterns)"]
        MemTools["memory__remember/recall/<br/>forget/list"]
        Recovery["AgentSessionCrashRecovery"]
        SentinelBox["AI.Sentinel"]
        Mcp["MCP servers<br/>roslyn, context7"]
        MemSvc["Thalos IMemoryService"]
        MemStore2["PostgresMemoryStore<br/>(AgentMemories)"]
        MemIndex["Rag.NET IMemoryIndex<br/>(rag_chunks)"]
        Adapter["ThalosLearningsMemory<br/>(shared owner 'daedalus')"]
        Reindex["ReindexPendingMemoriesHostedService"]
    end

    subgraph "Shared Infrastructure"
        DbCtx["ApplicationDbContext<br/>(AgentSessions, AgentMessages,<br/>AgentMemories, Tasks, …)"]
        Ollama["Ollama nomic-embed-text<br/>(768 dims)"]
        PG[("PostgreSQL 16<br/>pgvector/pgvector:pg16")]
    end

    Pages --> RalphCtrl
    AgentPage -->|REST + SSE| AgentCtrl
    RalphCtrl --> Ralph
    Ralph --> RalphLlm
    Ralph --> LearnPort
    LearnPort --> Adapter
    ConsoleComposition -.registers.-> Adapter
    AgentCtrl --> Runtime
    AgentCtrl --> MemSvc
    Composition -.registers.-> Runtime
    Composition -.registers.-> Recovery
    Composition -.registers.-> MemSvc
    Composition -.registers.-> Reindex
    Runtime --> SessionStore
    Runtime --> SentinelBox
    Runtime --> Knowledge
    Runtime --> Mcp
    Runtime --> MemTools
    MemTools --> MemSvc
    Adapter --> MemSvc
    MemSvc --> MemStore2
    MemSvc --> MemIndex
    Reindex --> MemSvc
    MemIndex --> Ollama
    MemIndex --> PG
    SessionStore --> DbCtx
    MemStore2 --> DbCtx
    Recovery --> DbCtx
    Ralph --> DbCtx
    DbCtx --> PG
```

---

## Key Architectural Principles

### 🏗️ **Layered Architecture**

- **Presentation Layer**: Controllers, APIs, UI components
- **Application Layer**: Commands, Queries, DTOs, Services (CQRS pattern)
- **Domain Layer**: Entities, Value Objects, Business Logic
- **Infrastructure Layer**: EF Core, Repositories, Persistence

### 🚀 **Performance First**

- Zero-allocation LINQ (ZLinq) for hot paths
- Memory pooling for temporary buffers
- Compiled queries for frequent operations
- Response compression with Gzip

### 🛣️ **Railway-Oriented Programming**

- `Result<T>` type for expected failures
- Functional composition with `Bind()` and `Map()`
- No exception throwing for flow control
- Clear success/failure paths

### 🔄 **Distributed Task Processing**

- Ralph Loop worker pattern for iterative LLM execution
- Optimistic locking with `CurrentSessionId` for task claiming
- Heartbeat monitoring for worker health
- Automatic stale task reclamation

### 📊 **Automatic Code Generation & Git Integration** ✨

- **Git Repository Manager** (`IGitRepositoryManager`):
    - Clone repositories from remote URLs with authentication support
    - Create isolated feature branches per task for safe operations
    - Automatic git worktree management for parallel execution
    - Atomic commit/push operations with PR creation
    - Full diff tracking for learning accumulation
- **Code Context Extraction** (`IRepositoryCodeExtractor`):
    - Extract complete file contents with line numbers for LLM analysis
    - Build rich semantic context from repository structure
    - Support for pattern-based file searches
    - Accumulate learnings from previous iterations in prompt
    - Directory tree extraction for navigation context
- **Fully Automated Workflow**:
    - LLM makes code changes directly to isolated worktrees
    - Diffs are automatically captured and included in learnings
    - Changes are automatically staged, committed, and pushed
    - Pull requests created with auto-generated descriptive messages
    - Cleanup and validation ensure zero orphaned resources

### 📊 **Data-Driven Development**

- Railway-Oriented patterns eliminate null checks
- Primary constructors reduce boilerplate
- Strong typing via Value Objects (Priority, Status, Complexity)
- Immutable entities with `readonly struct`

### ✅ **Quality Assurance**

- Static analysis: SonarAnalyzer, Meziantou, NetAnalyzers
- Comprehensive test coverage (Unit, Integration, E2E)
- Compile-time logging with `[LoggerMessage]`
- Structured logging throughout application

---

## Git Integration Service APIs

### IGitRepositoryManager Interface

Core abstraction for all git operations:

```csharp
// Repository Initialization
Task<Result<GitOperationContext>> CloneRepositoryAsync(
    string repoUrl,
    string? branch = null,
    string? targetPath = null,
    CancellationToken ct = default);

// Branch Management
Task<Result<string>> CreateFeatureBranchAsync(
    string workTreePath,
    string branchName,
    string? fromBranch = null,
    CancellationToken ct = default);

// Worktree Operations (for parallel execution)
Task<Result<string>> CreateWorktreeAsync(
    string baseRepoPath,
    string worktreeName,
    string branchName,
    CancellationToken ct = default);

Task<Result> DeleteWorktreeAsync(
    string worktreePath,
    CancellationToken ct = default);

// Change Tracking
Task<Result<IReadOnlyList<GitDiff>>> GetDiffsAsync(
    string workTreePath,
    string baseBranch,
    CancellationToken ct = default);

Task<Result> ApplyPatchAsync(
    string workTreePath,
    string patchContent,
    CancellationToken ct = default);

// Commit & Push
Task<Result> CommitChangesAsync(
    string workTreePath,
    string message,
    string? author = null,
    CancellationToken ct = default);

Task<Result> PushBranchAsync(
    string workTreePath,
    string branchName,
    bool force = false,
    CancellationToken ct = default);
```

### IRepositoryCodeExtractor Interface

Code analysis and context building:

```csharp
// Single file operations
Task<Result<string>> GetFileContentsAsync(
    string repositoryPath,
    string filePath,
    CancellationToken ct = default);

// Batch file operations
Task<Result<IReadOnlyDictionary<string, string>>> GetFilesAsync(
    string repositoryPath,
    IEnumerable<string> filePaths,
    CancellationToken ct = default);

// Directory navigation
Task<Result<RepositoryStructure>> GetDirectoryStructureAsync(
    string repositoryPath,
    string? directoryPath = null,
    CancellationToken ct = default);

// Pattern-based search
Task<Result<IReadOnlyList<string>>> SearchFilesAsync(
    string repositoryPath,
    string pattern,
    CancellationToken ct = default);
```

---

## Git Integration Workflow Summary

| Phase            | Operation                           | Outcome                          |
| ---------------- | ----------------------------------- | -------------------------------- |
| **Setup**        | Clone repository → Create worktree  | Isolated working directory ready |
| **Analysis**     | Extract code → Build context        | Full codebase sent to LLM        |
| **Iteration**    | Apply changes → Generate diffs      | Changes tracked for learnings    |
| **Verification** | Check completion promise            | Success/failure decision         |
| **Completion**   | Commit → Push → Create PR           | Changes available for review     |
| **Cleanup**      | Delete worktree → Release resources | Temp files cleaned up            |

This architecture enables **fully autonomous code generation** where the LLM iteratively refines code changes until completion criteria are met, with all changes tracked in git and presented as pull requests for human review.

- Heartbeat monitoring for worker health
- Automatic stale task reclamation

### 📊 **Data-Driven Development**

- Railway-Oriented patterns eliminate null checks
- Primary constructors reduce boilerplate
- Strong typing via Value Objects (Priority, Status, Complexity)
- Immutable entities with `readonly struct`

### ✅ **Quality Assurance**

- Static analysis: SonarAnalyzer, Meziantou, NetAnalyzers
- Comprehensive test coverage (Unit, Integration, E2E)
- Compile-time logging with `[LoggerMessage]`
- Structured logging throughout application
