# smplkit C# SDK

The official C# SDK for [smplkit](https://smplkit.com) — simple application infrastructure for developers.

## Installation

```bash
dotnet add package Smplkit.Sdk
```

## Requirements

- .NET 8.0+

## Quick Start

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

## Configuration

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
