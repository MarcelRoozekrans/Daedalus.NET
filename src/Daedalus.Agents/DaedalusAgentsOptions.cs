namespace Daedalus.Agents;

/// <summary>
///     Bound from the <c>Thalos</c> configuration section by <see cref="DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents"/>.
///     Agent definitions are declared in configuration so no redeploy is needed to add one; the Anthropic provider reads
///     its own <c>Thalos:Anthropic</c> subsection (see <c>Thalos.Anthropic.AnthropicOptions</c>).
/// </summary>
public sealed class DaedalusAgentsOptions
{
    /// <summary>Configuration section name: <c>Thalos</c>.</summary>
    public const string SectionName = "Thalos";

    /// <summary>Claude Code-style MCP config file. Relative paths resolve against the host content root. Missing file → no MCP tool sources.</summary>
    public string McpConfigPath { get; set; } = ".mcp.json";

    /// <summary>Agent definitions (<c>Thalos:Agents:N</c>).</summary>
    public IList<AgentConfig> Agents { get; } = [];

    /// <summary>Tool-policy bindings (<c>Thalos:ToolPolicies:N</c>): tools matching <see cref="ToolPolicyConfig.Pattern"/> require the named policy.</summary>
    public IList<ToolPolicyConfig> ToolPolicies { get; } = [];

    /// <summary>AI.Sentinel settings (<c>Thalos:Sentinel</c>).</summary>
    public SentinelConfig Sentinel { get; } = new();
}

/// <summary>One agent definition as declared in configuration.</summary>
public sealed class AgentConfig
{
    /// <summary>Stable id: a ULID (<c>01ARZ3NDEKTSV4RRFFQ69G5FAV</c>) or GUID string. Sessions reference it, so never change it after go-live.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name (1–64 chars).</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable description shown in the agent list.</summary>
    public string Description { get; set; } = "";

    /// <summary>System instructions sent on every model call.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Provider model id; <see langword="null"/> → <c>Thalos:Anthropic:DefaultModel</c>.</summary>
    public string? Model { get; set; }

    /// <summary>Per-call output token cap; <see langword="null"/> → provider default.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    ///     Glob allow-list over qualified tool names (<c>daedalus__*</c>, <c>roslyn__find_callers</c>). Empty (the binder appends
    ///     to a pre-populated list, so the default lives in the mapping, not here) → everything (<c>*</c>).
    /// </summary>
    public IList<string> Tools { get; } = [];
}

/// <summary>Binds a tool-name glob to a <c>[Policy]</c> name (for example <c>roslyn__apply_*</c> → <c>developer</c>).</summary>
public sealed class ToolPolicyConfig
{
    /// <summary>Glob over qualified tool names (<c>source__tool</c>).</summary>
    public string Pattern { get; set; } = "";

    /// <summary>The <c>[Policy("…")]</c> name that must pass.</summary>
    public string Policy { get; set; } = "";
}

/// <summary>AI.Sentinel settings. Actions are <c>PassThrough</c>, <c>Log</c>, <c>Alert</c> or <c>Quarantine</c>.</summary>
public sealed class SentinelConfig
{
    /// <summary>Whether the Sentinel decorator is registered at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Action for critical findings.</summary>
    public string OnCritical { get; set; } = "Quarantine";

    /// <summary>Action for high findings.</summary>
    public string OnHigh { get; set; } = "Alert";

    /// <summary>Action for medium findings.</summary>
    public string OnMedium { get; set; } = "Log";

    /// <summary>Action for low findings.</summary>
    public string OnLow { get; set; } = "Log";

    /// <summary>Simple type names of AI.Sentinel detectors to switch off (for example <c>SecretLeakDetector</c>). Unknown names fail at startup.</summary>
    public IList<string> DisabledDetectors { get; } = [];
}
