# smplkit SDK for .NET

See `~/.claude/CLAUDE.md` for universal rules (git workflow, testing, code quality, SDK conventions, etc.).

## Repository Structure

- `src/Smplkit/Internal/Generated/` — Auto-generated client code from OpenAPI specs. Do not edit manually.
- `src/Smplkit/` (excluding `Internal/Generated/`) — Hand-crafted SDK wrapper. This is the public API.

## Building

```bash
dotnet build
```

## Testing

```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Regenerating Clients

```bash
./scripts/generate.sh
```

## .NET Version Policy

The SDK targets .NET 8.0 (LTS).

## HTTP Client Architecture

All HTTP calls MUST go through the NSwag-generated client code in `Internal/Generated/`. The wrapper layer (`ConfigClient`, `FlagsClient`, `SmplClient`) calls generated clients via `ApiExceptionMapper.ExecuteAsync()`, never `HttpClient` or raw HTTP directly.

- **Do not** construct hand-coded HTTP requests, URLs, or use `HttpClient.GetAsync/PostAsync/etc.` in wrapper code
- **Do not** re-introduce a `Transport` class or equivalent raw HTTP abstraction
- Generated clients are instantiated in `GeneratedClientFactory` with shared `HttpClient` configuration
- `ApiExceptionMapper` translates NSwag `ApiException` into SDK exception types (`SmplException`, `SmplNotFoundException`, etc.)

## Coding Conventions

- Async/await throughout with `Task<T>` returns
- `CancellationToken` as optional parameter on EVERY async method
- `IDisposable` on the top-level client
- Nullable reference types enabled
- XML doc comments (`///`) on ALL public types and members
- `record` types for immutable response models
- `internal` visibility for generated code and implementation details
- PascalCase for public members, `_camelCase` for private fields
- Async methods suffixed with `Async` (e.g., `ListAsync`, `GetAsync`, `DeleteAsync`)
- JSON:API envelope parsing in the config client

## Package Naming

- **NuGet package name:** `Smplkit.Sdk` (install via `dotnet add package Smplkit.Sdk`)
- **Namespace:** `Smplkit` (import via `using Smplkit;`)
- **The old NuGet package `smplkit-csharp-sdk` is deprecated. All new versions publish as `Smplkit.Sdk`.**

## Publishing

- NuGet publishing uses OIDC trusted publishing (no long-lived API key).
- The `NuGet/login@v1` action exchanges a GitHub OIDC token for a short-lived NuGet API key.
- Publishing requires the `nuget` GitHub Actions environment.

## smplkit MCP server — operate the platform via MCP

smplkit runs a **hosted MCP server** at `https://mcp.smplkit.com/api/mcp` that operates the whole platform — feature flags, config, log levels, audit search, and scheduled jobs — exposed as MCP tools (source: the `mcp` repo). When a task means *operating* the platform (reading job runs, flipping a flag, changing a config value, setting a log level, searching the audit log) rather than editing service source, prefer these MCP tools over ad-hoc curl or one-off SDK scripts.

If it isn't connected yet, tell the user and offer to add it:

    claude mcp add --transport http smplkit https://mcp.smplkit.com/api/mcp

First connect does a one-time browser sign-in (Google/Microsoft, WorkOS AuthKit OAuth) and refreshes itself after that. A committed `.mcp.json` at each repo root advertises the same server so Claude Code / Cursor auto-detect it.
