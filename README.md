# smplkit SDK for .NET

The official [smplkit](https://docs.smplkit.com) SDK for .NET.

## Installation

```bash
dotnet add package Smplkit.Sdk
```

## Quick start

```csharp
using Smplkit;
using Smplkit.Config;

using var client = new SmplkitClient(new SmplkitClientOptions
{
    ApiKey = "sk_api_your_key_here",
});

// List all configs
var configs = await client.Config.ListAsync();

// Get a config by ID
var config = await client.Config.GetAsync("config-uuid");

// Get a config by key
var byKey = await client.Config.GetByKeyAsync("user_service");

// Create a new config
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
await client.Config.DeleteAsync("config-uuid");
```

## Configuration

```csharp
var options = new SmplkitClientOptions
{
    ApiKey = "sk_api_your_key_here",            // Required
    BaseUrl = "https://config.smplkit.com",     // Default
    Timeout = TimeSpan.FromSeconds(30),         // Default
};
```

## Error handling

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
    Console.WriteLine($"Status: {ex.StatusCode}");     // 404
    Console.WriteLine($"Body: {ex.ResponseBody}");
}
catch (SmplConflictException ex)
{
    // HTTP 409 — e.g., deleting a config with children
}
catch (SmplValidationException ex)
{
    // HTTP 422 — validation errors
}
catch (SmplConnectionException ex)
{
    // Network connectivity issues
}
catch (SmplTimeoutException ex)
{
    // Request timed out
}
```

## Cancellation

All async methods accept an optional `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var configs = await client.Config.ListAsync(cts.Token);
```

## Documentation

Full documentation is available at [docs.smplkit.com](https://docs.smplkit.com).
