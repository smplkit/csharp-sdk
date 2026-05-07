// Demonstrates the smplkit runtime SDK for Smpl Audit.
//
// Covers: event record / list / get, plus the SIEM forwarders surface
// (create / list / delete + the test_forwarder/execute proxy + a
// DoNotForward event flow). The forwarders portion gracefully skips
// on 402 (free / standard tier) so the showcase remains runnable in
// any environment.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/AuditRuntimeShowcase

using Smplkit;
using Smplkit.Audit;

// create the client
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});

// record an event
var someResourceId = "showcase-" + Guid.NewGuid().ToString("N").Substring(0, 8);
client.Audit.Events.Record(new CreateEventInput
{
    Action = "invoice.created",
    ResourceType = "invoice",
    ResourceId = someResourceId,
    OccurredAt = DateTimeOffset.UtcNow,
    Snapshot = new Dictionary<string, object?>
    {
        ["total_cents"] = 4900,
        ["currency"] = "USD",
    },
    Data = new Dictionary<string, object?>
    {
        ["request_id"] = "req-abc",
    },
});

// force the event to be posted (normally happens automatically, in the
// background, but we want to force it to be written now for this demo)
await client.Audit.Events.FlushAsync(TimeSpan.FromSeconds(2));

// list events
var page = await client.Audit.Events.ListAsync(new ListEventsInput
{
    ResourceType = "invoice",
    ResourceId = someResourceId,
    PageSize = 10,
});
Console.WriteLine($"Found {page.Events.Count} events for {someResourceId}:");
foreach (var ev in page.Events)
{
    Console.WriteLine($"  {ev.Action}  id={ev.Id}  actor={ev.ActorType}");
}

if (page.Events.Count != 1)
{
    throw new Exception($"Expected 1 event, got {page.Events.Count}");
}

// fetch an event by ID
var first = await client.Audit.Events.GetAsync(page.Events[0].Id);
Console.WriteLine($"Round-tripped: {first.Action} at {first.OccurredAt}");

// Forwarders (Pro tier — gracefully skip on 402)
Forwarder? fwd = null;
try
{
    fwd = await client.Audit.Forwarders.CreateAsync(new CreateForwarderInput
    {
        Name = "showcase-" + Guid.NewGuid().ToString("N").Substring(0, 6),
        ForwarderType = "http",
        Http = new ForwarderHttp
        {
            Url = "https://httpbin.org/post",
            Headers = new List<HttpHeader> { new("X-Showcase", "ok") },
        },
    });
    Console.WriteLine($"Created forwarder: {fwd.Slug}");
}
catch (Smplkit.Internal.Generated.Audit.ApiException ex) when (ex.StatusCode == 402)
{
    Console.WriteLine("Skipping forwarder showcase — account is not Pro tier");
    Console.WriteLine("Done!");
    return;
}

try
{
    // DoNotForward suppresses the forward but still records the
    // skip in the delivery log.
    client.Audit.Events.Record(new CreateEventInput
    {
        Action = "invoice.created",
        ResourceType = "invoice",
        ResourceId = someResourceId + "-skipped",
        DoNotForward = true,
    });
    await client.Audit.Events.FlushAsync(TimeSpan.FromSeconds(2));

    // Test the destination via the proxy
    var test = await client.Audit.Functions.ExecuteTestForwarderAsync(new TestForwarderInput
    {
        Url = "https://httpbin.org/post",
        Body = "{\"hello\":\"world\"}",
        TimeoutMs = 5000,
    });
    Console.WriteLine($"test_forwarder: succeeded={test.Succeeded} status={test.ResponseStatus}");
}
finally
{
    await client.Audit.Forwarders.DeleteAsync(fwd.Id);
    Console.WriteLine($"Deleted forwarder: {fwd.Slug}");
}

Console.WriteLine("Done!");
