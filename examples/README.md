# smplkit SDK Examples

Runnable examples demonstrating the [smplkit C# SDK](https://github.com/smplkit/csharp-sdk).

> **Note:** These examples require valid smplkit credentials and a live environment — they are not self-contained demos.

## Prerequisites

1. [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.
2. A valid smplkit API key (create one in the [smplkit console](https://app.smplkit.com)).
3. At least one config created in your smplkit account (every account comes with a `common` config by default).

## Config Showcase

**File:** [`ConfigShowcase/Program.cs`](ConfigShowcase/Program.cs)

An end-to-end walkthrough of the Smpl Config SDK covering:

- **Client initialization** — `SmplClient` with `SmplClientOptions`
- **Management-plane CRUD** — create, update, list, get by key, and delete configs
- **Environment overrides** — `SetValuesAsync()` and `SetValueAsync()` for per-environment configuration
- **Multi-level inheritance** — child → parent → common hierarchy setup
- **Runtime value resolution** — `ConnectAsync()`, `Get()`, typed accessors (`GetString`, `GetInt`, `GetBool`)
- **Real-time updates** — WebSocket-driven cache invalidation with change listeners
- **Manual refresh and cache diagnostics** — `RefreshAsync()`, `Stats()`
- **Cancellation** — all async methods accept `CancellationToken`

### Running

```bash
export SMPLKIT_API_KEY="sk_api_..."
dotnet run --project examples/ConfigShowcase
```

The script creates temporary configs, exercises all SDK features, then cleans up after itself.
