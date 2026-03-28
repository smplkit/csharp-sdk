# smplkit SDK Examples

Runnable examples demonstrating the [smplkit C# SDK](https://github.com/smplkit/csharp-sdk).

> **Note:** These examples require valid smplkit credentials and a live environment — they are not self-contained demos.

## Prerequisites

1. [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

2. A valid smplkit API key (create one in the [smplkit console](https://www.smplkit.com)).

3. At least one config created in your smplkit account (every account comes with a `common` config by default).

## Config Showcase

**File:** [`ConfigShowcase/Program.cs`](ConfigShowcase/Program.cs)

An end-to-end walkthrough of the Smpl Config SDK covering:

- **Client initialization** — `SmplkitClient` with `SmplkitClientOptions`
- **Management-plane CRUD** — create, update, list, and delete configs
- **Multi-level inheritance** — child → parent → common hierarchy setup
- **Cleanup** — delete child configs and reset common to empty state

> **Note:** The C# SDK currently implements the management plane only. Sections for runtime value resolution (ConnectAsync), real-time WebSocket updates, and environment comparison are stubbed with comments describing the expected API surface when those features ship.

### Running

```bash
export SMPLKIT_API_KEY="sk_api_..."
dotnet run --project examples/ConfigShowcase
```

The script creates temporary configs, exercises management-plane SDK features, then cleans up after itself.
