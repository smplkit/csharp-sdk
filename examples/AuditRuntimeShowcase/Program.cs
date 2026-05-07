// Demonstrates the smplkit runtime SDK for Smpl Audit.
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
await client.Audit.Events.FlushAsync(TimeSpan.FromMilliseconds(200));

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

Console.WriteLine("Done!");
