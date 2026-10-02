using Thalos.Sandbox.Host;

// The per-run sandbox: Thalos's in-container host, nothing Daedalus-specific. It holds no secrets and runs agent-written
// build files; see docs/superpowers/specs/2026-10-01-phase-2.6-sandboxed-run-pods-design.md.
var app = SandboxHost.Map(SandboxHost.CreateBuilder(args).Build());
await app.RunAsync();
