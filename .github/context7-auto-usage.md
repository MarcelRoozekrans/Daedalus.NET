# Context7 MCP Auto-Usage Instructions

## Automatic Context7 Library Documentation Lookups

When the user asks any question related to a library, framework, package, or library-specific functionality, automatically use the Context7 MCP server tools without requiring the user to explicitly mention it.

### Trigger Conditions

Automatically use Context7 MCP when the user:

- **Asks about a specific library or framework** (e.g., "How do I use async/await in this library?", "What's the best way to handle errors in Express.js?")
- **Asks for documentation** (e.g., "How do I configure X?", "What are the parameters for Y function?")
- **Asks for code examples** (e.g., "Show me how to do X with this library")
- **Asks about best practices** for a specific library (e.g., "What's the recommended pattern for Z?")
- **Asks about library versions or APIs** (e.g., "Is this method still available in version 5?")
- **References library-specific concepts** (e.g., "How do middleware work in Express?")

### Execution Pattern

1. **Identify the library** - Determine which library/package the user is asking about
2. **Call `mcp_context7_resolve-library-id`** - Resolve the library name to a Context7-compatible ID
3. **Call `mcp_context7_query-docs`** - Query the official documentation using the resolved ID
4. **Synthesize the answer** - Provide the user with accurate, up-to-date information from official sources

**Important:** Only call Context7 when it's clearly a library/documentation question. Avoid calling for:

- General programming concepts not specific to a library
- Code review or refactoring advice
- Architecture or design pattern discussions (unless asking about library-specific patterns)
- Debugging user's custom code (unless the issue is clearly library-related)

### Response Format

When using Context7:

- Present accurate information from official sources
- Include code examples when available
- Reference the documentation source where appropriate
- Maintain accuracy over brevity - official docs are authoritative

### No User Notification Required

Do NOT inform the user that you're using the Context7 MCP server. Simply provide the answer naturally as if you already knew it. The MCP integration should be transparent to the user.

### Example Scenarios

✅ **AUTO-USE Context7:**

- "How do I configure authentication in Next.js?"
- "What's the proper way to handle errors with zod?"
- "Show me an example of using useEffect in React"
- "How do I stream responses in Express?"
- "What are the best practices for database queries in Prisma?"

❌ **DO NOT auto-use Context7:**

- "Help me debug why my code isn't working" (unless clearly library-specific)
- "What's the best way to structure my project?"
- "Explain how REST APIs work"
- "How do I think about this architectural problem?"
