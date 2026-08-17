# MCP Knowledge Base Design

## Goal

Give the Ralph Loop LLM on-demand, semantic access to learnings and failure patterns via local MCP tools, replacing the current approach of dumping all knowledge into the prompt text.

## Architecture

**Approach:** Hybrid -- keep the core text prompt (task, completion promise, workspace context, iteration counter) but replace the bulk learnings injection with two MCP tools backed by pgvector semantic search.

**Key components:**
- In-process MCP server using `ModelContextProtocol` C# SDK
- pgvector extension on PostgreSQL for vector similarity search
- Ollama container with `nomic-embed-text` for embedding generation
- Graceful three-level fallback to current behavior when components are unavailable

## Data Flow

```
CURRENT:
  DB → LearningsEnrichmentMiddleware → Full text dump → Prompt → LLM
  (All learnings + all patterns injected every iteration)

NEW:
  DB → LearningsEnrichmentMiddleware → Brief summary + tool hint → Prompt → LLM
  LLM → search_learnings(query) → pgvector similarity → Relevant results
  LLM → search_failure_patterns(error) → pgvector similarity → Matching patterns
  (LLM pulls what it needs, when it needs it)
```

## MCP Tool Definitions

### search_learnings

Search past learnings from previous task executions using semantic similarity. The LLM should use this when it encounters errors, needs context about the codebase, or wants to learn from previous approaches that worked or failed.

**Parameters:**
- `query` (string, required): Natural language description of what to search for.
- `project_id` (string, optional): Filter to learnings from a specific project.
- `max_results` (int, optional, default: 5): Maximum number of results.

**Returns:** JSON array of matching learnings with content, source task, category, relevance score, and timestamp.

### search_failure_patterns

Search known failure patterns and their solutions. The LLM should use this when it encounters a build error, test failure, or runtime exception.

**Parameters:**
- `error_message` (string, required): The error message or pattern to search for.
- `max_results` (int, optional, default: 3): Maximum number of results.

**Returns:** JSON array of matching patterns with pattern text, known solution, occurrence count, last seen date, and relevance score.

## Data Model Changes

### Embedding Columns

Add a `vector(384)` column to both the Learnings and FailurePatterns tables. Dimension 384 matches `nomic-embed-text` / `all-minilm-l6-v2` output.

```sql
-- Requires: CREATE EXTENSION IF NOT EXISTS vector;
ALTER TABLE Learnings ADD COLUMN embedding vector(384);
CREATE INDEX idx_learnings_embedding ON Learnings
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

ALTER TABLE FailurePatterns ADD COLUMN embedding vector(384);
CREATE INDEX idx_failure_patterns_embedding ON FailurePatterns
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 50);
```

### Embedding Generation

- **On save:** When a learning or failure pattern is created/updated, generate an embedding via `IEmbeddingGenerator<string, Embedding<float>>` and store it.
- **On query:** Generate an embedding for the search query, then use pgvector's `<=>` operator for cosine distance ordering.

## Aspire Integration

The AppHost gains two new resources:

1. **Ollama container** with `nomic-embed-text` model:
   - Pulled automatically on first start
   - Connection reference passed to API service

2. **pgvector extension** enabled on the PostgreSQL container:
   - `CREATE EXTENSION IF NOT EXISTS vector` in migration

## Integration with Existing Infrastructure

### McpToolBuilder -- Local Server Support

The `McpToolBuilder` gains support for `Type: "local"` servers. Instead of creating a stdio/HTTP transport, it resolves `McpServerTool` instances from DI-registered tool classes.

**Configuration:**
```json
{
  "ExternalServices": {
    "Mcp": {
      "Servers": {
        "context7": { "Type": "stdio", ... },
        "daedalus-knowledge": {
          "Type": "local",
          "Tools": ["*"]
        }
      }
    }
  }
}
```

### Tool Classes

Located in `src/Daedalus.Infrastructure/Agents/Tools/`:

- `DaedalusLearningsTools.cs` -- `[McpServerToolType]` class with constructor injection for `ILearningsRepository` and `IEmbeddingGenerator`. Contains the `search_learnings` method with `[McpServerTool]` attribute.

- `DaedalusFailurePatternsTools.cs` -- `[McpServerToolType]` class with constructor injection for `IFailurePatternDatabase` and `IEmbeddingGenerator`. Contains the `search_failure_patterns` method.

### LearningsEnrichmentMiddleware Changes

The middleware switches between two modes:

- **MCP tools available:** Inject a brief summary ("Knowledge base: N learnings available. Use search_learnings and search_failure_patterns tools.") instead of the full text dump.
- **MCP tools unavailable (fallback):** Current behavior -- inject all learnings and patterns as text into the prompt.

### Pipeline -- No Changes

The middleware order stays the same. No middleware is added or removed:

| Middleware | Order | Change |
|---|---|---|
| LearningsEnrichmentMiddleware | 90 | Inject summary vs full text (conditional) |
| PromptBuildingMiddleware | 100 | No change |
| LlmInvocationMiddleware | 200 | No change (tools auto-attached by factory) |
| All others | 250+ | No change |

## Fallback Strategy

Three-level graceful degradation ensures the Ralph Loop never breaks:

### Level 1: Ollama Unavailable

- `IEmbeddingGenerator` registration checks Ollama health at startup
- If unreachable: register `NoOpEmbeddingGenerator` returning empty vectors
- MCP tools fall back to PostgreSQL full-text search (`to_tsvector` / `ts_rank`) for learnings and `ILIKE` pattern matching for failure patterns
- Tools still work, just with keyword matching instead of semantic

### Level 2: MCP Tools Fail to Register

- `McpToolBuilder` already has try/catch per server (logs and skips unavailable servers)
- If `daedalus-knowledge` fails: no tools attached to the agent
- `LearningsEnrichmentMiddleware` detects whether tools registered
- Falls back to current full-text injection behavior
- Logs warning: "Knowledge base MCP tools unavailable, falling back to prompt injection"

### Level 3: Tool Call Fails at Runtime

- MCP tool methods wrap all DB calls in try/catch
- On failure: return structured error message the LLM can understand
- Example: `"Error: Knowledge base temporarily unavailable. Proceed with available context."`
- LLM continues without the tool result

## New Dependencies

| Package | Purpose |
|---|---|
| `ModelContextProtocol` | MCP server SDK for tool attributes and registration |
| `Pgvector` / `Pgvector.EntityFrameworkCore` | pgvector support for EF Core |
| `Microsoft.Extensions.AI` | `IEmbeddingGenerator` abstraction (already referenced) |
| Ollama container | `nomic-embed-text` embedding model |

## File Changes Summary

**New files:**
- `src/Daedalus.Infrastructure/Agents/Tools/DaedalusLearningsTools.cs`
- `src/Daedalus.Infrastructure/Agents/Tools/DaedalusFailurePatternsTools.cs`
- `src/Daedalus.Infrastructure/Services/NoOpEmbeddingGenerator.cs`
- EF Core migration for vector columns and pgvector extension

**Modified files:**
- `src/Daedalus.Infrastructure/Agents/McpToolBuilder.cs` -- Add local server type support
- `src/Daedalus.Application/Services/Middleware/LearningsEnrichmentMiddleware.cs` -- Conditional slim/full injection
- `src/Daedalus.Infrastructure/Persistence/LearningsRepository.cs` -- Add vector search methods
- `src/Daedalus.Infrastructure/Persistence/Configurations/` -- Add vector column config
- `src/Daedalus.AppHost/Program.cs` -- Add Ollama container, enable pgvector
- `src/Daedalus.Api/appsettings.json` -- Add daedalus-knowledge MCP server config
- `src/Daedalus.Api/Program.cs` -- Register embedding generator
- `src/Daedalus.Infrastructure/Extensions/InfrastructureServiceExtensions.cs` -- Register tool classes

## Testing Strategy

- **Unit tests:** Tool classes with mocked repositories and embedding generator
- **Integration tests:** pgvector search with test embeddings in Testcontainers PostgreSQL
- **Fallback tests:** Verify each degradation level works correctly
- **Pipeline tests:** Verify LearningsEnrichmentMiddleware switches modes correctly
