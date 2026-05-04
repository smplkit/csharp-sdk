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
    Service = "my-service",
});

// Runtime: resolve config values for the current environment (lazy-loaded, cached)
var values = client.Config.Get("user_service");
var timeout = values["timeout"];

// Typed deserialization
var cfg = client.Config.Get<MyServiceConfig>("user_service");

// Management: create, get, list, delete
var newConfig = client.Manage.Config.New(
    id: "my_service",
    name: "My Service",
    description: "Configuration for my service");
newConfig.SetNumber("timeout", 30);
newConfig.SetNumber("retries", 3);
await newConfig.SaveAsync();

var existing = await client.Manage.Config.GetAsync("user_service");
var all = await client.Manage.Config.ListAsync();
await client.Manage.Config.DeleteAsync("my_service");
```

## Flags

Feature flags with local JSON Logic evaluation, typed handles, and real-time updates:

```csharp
using Smplkit;
using Smplkit.Flags;

using var client = new SmplClient(new SmplClientOptions
{
    ApiKey = "sk_api_...",
    Environment = "production",
    Service = "my-service",
});

// Declare typed flag handles with code-level defaults
var checkout = client.Flags.BooleanFlag("checkout-v2", false);
var banner = client.Flags.StringFlag("banner-color", "red");
var retries = client.Flags.NumberFlag("max-retries", 3);

// Set ambient evaluation context once (e.g. from request middleware).
// The returned IDisposable reverts the context when disposed.
using var _ = client.SetContext(new[]
{
    new Context("user", currentUser.Id, new Dictionary<string, object?>
    {
        ["plan"] = currentUser.Plan,
    }),
    new Context("account", currentAccount.Id, new Dictionary<string, object?>
    {
        ["region"] = currentAccount.Region,
    }),
});

// Evaluate flags — local, typed, instant (no network per call).
// Definitions are fetched and the WebSocket opens on first .Get(); no
// explicit Connect call is required.
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
client.Flags.OnChange(e => Console.WriteLine($"Flag {e.Id} changed via {e.Source}"));
```

### Flag Management

```csharp
// Create a flag
var flag = client.Manage.Flags.NewBooleanFlag(
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
var existing = await client.Manage.Flags.GetAsync("checkout-v2");
var all = await client.Manage.Flags.ListAsync();
await client.Manage.Flags.DeleteAsync("checkout-v2");
```

## Logging

Dynamic, server-managed log levels for every category in your host's logging
pipeline. Wire smplkit into Microsoft.Extensions.Logging once and every
`ILogger<T>` in your app — including ones created before smplkit starts —
becomes auto-discovered and level-managed. Server-pushed level changes gate
output across **every** registered provider (Console, Debug, file sinks,
Serilog-via-MEL, etc.) — not just smplkit's own output.

```csharp
using Microsoft.Extensions.Logging;
using Smplkit;
using Smplkit.Logging.Adapters;

using var client = new SmplClient(new SmplClientOptions
{
    ApiKey = "sk_api_...",
    Environment = "production",
    Service = "my-service",
});

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddSmplkit(client);   // registers as ILoggerProvider +
                                  // IConfigureOptions<LoggerFilterOptions> +
                                  // IOptionsChangeTokenSource<...>
});

// Bulk-register every discovered category and apply current managed levels.
await client.Logging.InstallAsync();

// Any logger created in your app from this point on is auto-discovered.
var dbLogger = loggerFactory.CreateLogger("MyApp.Db");
dbLogger.LogInformation("Hello — server-managed level controls whether this is emitted");

// Re-fetch managed levels and re-apply them (rarely needed; the WebSocket
// pushes changes automatically).
await client.Logging.RefreshAsync();

// Listen for level changes
client.Logging.OnChange(e =>
    Console.WriteLine($"Logger {e.Id} level → {e.Level} via {e.Source}"));
```

Inside an ASP.NET Core / Generic Host app the wiring is the same — the
`ILoggingBuilder` exposed by `services.AddLogging(b => …)` accepts
`AddSmplkit(client)` directly.

### Logger Management

```csharp
// Create / update a managed logger
var logger = client.Manage.Loggers.New("MyApp.Db");
logger.SetLevel(LogLevel.Warn);
await logger.SaveAsync();

// Fetch, list, delete
var existing = await client.Manage.Loggers.GetAsync("MyApp.Db");
var all = await client.Manage.Loggers.ListAsync();
await client.Manage.Loggers.DeleteAsync("MyApp.Db");

// Bulk-register explicit logger sources (e.g. seeding a fresh account or
// migrating fixtures across services). Defaults to immediate POST; pass
// flush=false to buffer multiple calls and FlushAsync to drain.
await client.Manage.Loggers.RegisterAsync(new[]
{
    new LoggerSource("MyApp.Db", level: LogLevel.Info),
    new LoggerSource("MyApp.Payments", level: LogLevel.Warn),
});

// Log groups follow the same shape (use to scope levels across many loggers).
var billing = client.Manage.LogGroups.New("billing");
billing.SetLevel(LogLevel.Debug);
await billing.SaveAsync();
```

### Serilog

Serilog's single-root logger model has no equivalent of MEL's per-provider
factory, so smplkit-managed Serilog loggers must be wired explicitly via
`SerilogAdapter.GetOrCreateSwitch`:

```csharp
using Serilog;
using Smplkit.Logging.Adapters;

var adapter = new SerilogAdapter();
client.Logging.RegisterAdapter(adapter);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("MyApp.Db", adapter.GetOrCreateSwitch("MyApp.Db"))
    .MinimumLevel.Override("MyApp.Payments", adapter.GetOrCreateSwitch("MyApp.Payments"))
    .WriteTo.Console()
    .CreateLogger();

await client.Logging.InstallAsync();
```

## Configuration

All settings are resolved from three sources, in order of precedence:

1. **Constructor options** — highest priority, always wins.
2. **Environment variables** — e.g. `SMPLKIT_API_KEY`, `SMPLKIT_ENVIRONMENT`.
3. **Configuration file** (`~/.smplkit`) — INI-format with profile support.
4. **Defaults** — built-in SDK defaults.

### Configuration File

The `~/.smplkit` file supports a `[common]` section (applied to all profiles) and named profiles:

```ini
[common]
environment = production
service = my-app

[default]
api_key = sk_api_abc123

[local]
base_domain = localhost
scheme = http
api_key = sk_api_local_xyz
environment = development
debug = true
```

### Constructor Examples

```csharp
// Use a named profile
var client = new SmplClient(new SmplClientOptions { Profile = "local" });

// Or configure explicitly
var client = new SmplClient(new SmplClientOptions
{
    ApiKey = "sk_api_...",
    Environment = "production",
    Service = "my-service",
});
```

For the complete configuration reference, see the [Configuration Guide](https://docs.smplkit.com/getting-started/configuration).

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
