# smplkit C# SDK

[![NuGet Version](https://img.shields.io/nuget/v/Smplkit.Sdk)](https://www.nuget.org/packages/Smplkit.Sdk) [![Build](https://github.com/smplkit/csharp-sdk/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/smplkit/csharp-sdk/actions) [![Coverage](https://codecov.io/gh/smplkit/csharp-sdk/branch/main/graph/badge.svg)](https://codecov.io/gh/smplkit/csharp-sdk) [![License](https://img.shields.io/github/license/smplkit/csharp-sdk)](LICENSE) [![Docs](https://img.shields.io/badge/docs-docs.smplkit.com-blue)](https://docs.smplkit.com)

The official C# SDK for [smplkit](https://www.smplkit.com) — simple application infrastructure that just works.

## Installation

```bash
dotnet add package Smplkit.Sdk
```

## Requirements

- .NET 8.0+

## Quick Start

```csharp
using Smplkit;

// Option 1: Explicit API key
var client = new SmplClient(new SmplClientOptions { ApiKey = "sk_api_..." });

// Option 2: Environment variable (SMPLKIT_API_KEY)
// export SMPLKIT_API_KEY=sk_api_...
var client2 = new SmplClient();

// Option 3: Configuration file (~/.smplkit)
// [default]
// api_key = sk_api_...
var client3 = new SmplClient();
```

```csharp
using Smplkit;
using Smplkit.Config;

using var client = new SmplClient(new SmplClientOptions
{
    ApiKey = "sk_api_...",
    Environment = "production",
});

// Runtime: resolve config values for the current environment (lazy-loaded, cached)
var values = client.Config.Get("user_service");
var timeout = values["timeout"];

// Typed deserialization
var cfg = client.Config.Get<MyServiceConfig>("user_service");

// Management: create, get, list, delete
var newConfig = client.Config.Management.New(
    id: "my_service",
    name: "My Service",
    description: "Configuration for my service");
newConfig.Items = new Dictionary<string, object?> { ["timeout"] = 30, ["retries"] = 3 };
await newConfig.SaveAsync();

var existing = await client.Config.Management.GetAsync("user_service");
var all = await client.Config.Management.ListAsync();
await client.Config.Management.DeleteAsync("my_service");
```

## Flags

Feature flags with local JSON Logic evaluation, typed handles, and real-time updates:

```csharp
using Smplkit;
using Smplkit.Flags;

using var client = new SmplClient(new SmplClientOptions { ApiKey = "sk_api_..." });

// Declare typed flag handles with code-level defaults
var checkout = client.Flags.BoolFlag("checkout-v2", false);
var banner = client.Flags.StringFlag("banner-color", "red");
var retries = client.Flags.NumberFlag("max-retries", 3);

// Register a context provider (called on every flag evaluation)
client.Flags.SetContextProvider(() => new List<Context>
{
    new("user", currentUser.Id, new Dictionary<string, object?>
    {
        ["plan"] = currentUser.Plan,
    }),
    new("account", currentAccount.Id, new Dictionary<string, object?>
    {
        ["region"] = currentAccount.Region,
    }),
});

// Connect to an environment — fetches definitions, opens WebSocket
await client.Flags.ConnectAsync("production");

// Evaluate flags — local, typed, instant (no network per call)
if (checkout.Get())
    RenderNewCheckout();

var color = banner.Get();         // "blue", "red", etc.
var maxRetries = retries.Get();   // 5, 3, etc.

// Explicit context override (for background jobs, tests)
var result = checkout.Get(new List<Context>
{
    new("user", "test-user", new Dictionary<string, object?> { ["plan"] = "free" }),
});

// Listen for real-time changes
client.Flags.OnChange(e => Console.WriteLine($"Flag {e.Key} changed via {e.Source}"));

// Disconnect when done
await client.Flags.DisconnectAsync();
```

### Flag Management

```csharp
// Create a flag
var flag = client.Flags.Management.NewBooleanFlag(
    "checkout-v2", defaultValue: false,
    name: "Checkout V2", description: "New checkout experience");

// Add a rule and save
flag.AddRule(
    new Rule("Enable for enterprise users")
        .Environment("production")
        .When("user.plan", "==", "enterprise")
        .Serve(true)
        .Build());
await flag.SaveAsync();

// Fetch, list, delete
var existing = await client.Flags.Management.GetAsync("checkout-v2");
var all = await client.Flags.Management.ListAsync();
await client.Flags.Management.DeleteAsync("checkout-v2");
```

## Configuration

The API key is resolved using the following priority:

1. **Explicit argument:** Set `ApiKey` in `SmplClientOptions`.
2. **Environment variable:** Set `SMPLKIT_API_KEY`.
3. **Configuration file:** Add `api_key` under `[default]` in `~/.smplkit`:

```ini
# ~/.smplkit

[default]
api_key = sk_api_your_key_here
```

If none of these are set, the SDK throws `SmplException` with a message listing all three methods.

```csharp
var options = new SmplClientOptions
{
    ApiKey = "sk_api_...",
    Timeout = TimeSpan.FromSeconds(30),  // default
};
```

## Error Handling

All SDK errors extend `SmplException`:

```csharp
using Smplkit.Errors;

try
{
    var config = await client.Config.Management.GetAsync("nonexistent-id");
}
catch (SmplNotFoundException ex)
{
    Console.WriteLine($"Not found: {ex.Message}");
}
catch (SmplException ex)
{
    Console.WriteLine($"Status: {ex.StatusCode}, Body: {ex.ResponseBody}");
}
```

| Exception                  | Cause                        |
|----------------------------|------------------------------|
| `SmplNotFoundException`    | HTTP 404 — resource not found |
| `SmplConflictException`   | HTTP 409 — conflict           |
| `SmplValidationException` | HTTP 422 — validation error   |
| `SmplTimeoutException`    | Request timed out             |
| `SmplConnectionException` | Network connectivity issue    |
| `SmplException`           | Any other SDK error           |

## Cancellation

All async methods accept an optional `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var configs = await client.Config.Management.ListAsync(cts.Token);
```

## Debug Logging

Set the `SMPLKIT_DEBUG` environment variable to enable verbose diagnostic output to stderr:

```bash
export SMPLKIT_DEBUG=1
```

Accepted truthy values: `1`, `true`, `yes` (case-insensitive). All other values (including unset) disable output.

Each line follows the format:

```
[smplkit:{subsystem}] {ISO-8601 timestamp} {message}
```

Subsystems: `lifecycle`, `websocket`, `api`, `discovery`, `resolution`, `adapter`, `registration`.

Output writes directly to `Console.Error` to avoid interference with the managed logging infrastructure.

## Documentation

- [Getting Started](https://docs.smplkit.com/getting-started)
- [C# SDK Guide](https://docs.smplkit.com/sdks/csharp)
- [API Reference](https://docs.smplkit.com/api)

## License

MIT
