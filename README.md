# smplkit C# SDK

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
});

// Get a config by key
var config = await client.Config.GetByKeyAsync("user_service");

// List all configs
var configs = await client.Config.ListAsync();

// Create a config
var newConfig = await client.Config.CreateAsync(new CreateConfigOptions
{
    Name = "My Service",
    Key = "my_service",
    Description = "Configuration for my service",
    Values = new Dictionary<string, object?>
    {
        ["timeout"] = 30,
        ["retries"] = 3,
    },
});

// Delete a config
await client.Config.DeleteAsync(newConfig.Id);
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
var flag = await client.Flags.CreateAsync(
    "checkout-v2", "Checkout V2", FlagType.Boolean, false,
    description: "New checkout experience");

// Add a rule
await flag.AddRuleAsync(
    new Rule("Enable for enterprise users")
        .Environment("production")
        .When("user.plan", "==", "enterprise")
        .Serve(true)
        .Build());

// Context types
var ct = await client.Flags.CreateContextTypeAsync("user", "User");
await client.Flags.UpdateContextTypeAsync(ct.Id,
    new Dictionary<string, object?> { ["plan"] = new Dictionary<string, object?>() });
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
    var config = await client.Config.GetAsync("nonexistent-id");
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
var configs = await client.Config.ListAsync(cts.Token);
```

## Documentation

- [Getting Started](https://docs.smplkit.com/getting-started)
- [C# SDK Guide](https://docs.smplkit.com/sdks/csharp)
- [API Reference](https://docs.smplkit.com/api)

## License

MIT
