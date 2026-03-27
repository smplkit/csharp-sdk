# smplkit SDK for .NET

## Repository structure

Two-layer architecture (mirrors the Python SDK):
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

Target 90%+ coverage on the SDK wrapper layer. Generated code coverage is not enforced.

## Regenerating clients

```bash
./scripts/generate.sh
```

This regenerates ALL clients from ALL specs in `openapi/`. Do NOT edit files under `Internal/Generated/` manually — they will be overwritten on next generation.

## Commits

Commit directly to main with conventional commit messages. No branches or PRs.
Exception: automated regeneration PRs from source repos use `regen/` branches by design.

## .NET Version Policy

The SDK targets .NET 8.0 (LTS).

## Coding conventions

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

- Publishing is automated via GitHub Actions CI/CD on merge to main
- Semantic-release handles version tagging based on conventional commits
- NuGet publishing uses OIDC trusted publishing (no long-lived API key)
- The `NuGet/login@v1` action exchanges a GitHub OIDC token for a short-lived NuGet API key
- Publishing requires the `nuget` GitHub Actions environment
