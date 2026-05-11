// Demonstrates the smplkit runtime SDK for Smpl Audit.
//
// Covers: event record / list / get, resource_types list, and actions list.
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
var someResourceId = "showcase-" + Guid.NewGuid().ToString("N")[..8];
client.Audit.Events.Record(new CreateEventInput
{
    Action = "invoice.created",
    ResourceType = "invoice",
    ResourceId = someResourceId,
    OccurredAt = DateTimeOffset.UtcNow,
    Data = new Dictionary<string, object?>
    {
        ["snapshot"] = new Dictionary<string, object?>
        {
            ["total_cents"] = 4900,
            ["currency"] = "USD",
        },
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
System.Diagnostics.Debug.Assert(page.Events.Count == 1, $"Expected 1 event, got {page.Events.Count}");

// fetch an event by ID
var first = await client.Audit.Events.GetAsync(page.Events[0].Id);
Console.WriteLine($"Round-tripped: {first.Action} at {first.OccurredAt}");
System.Diagnostics.Debug.Assert(first.Action == "invoice.created");
System.Diagnostics.Debug.Assert(first.ResourceType == "invoice");
System.Diagnostics.Debug.Assert(first.ResourceId == someResourceId);

// list distinct resource types (at least "invoice" should be present now)
var rtPage = await client.Audit.ResourceTypes.ListAsync();
Console.WriteLine($"Resource types ({rtPage.ResourceTypes.Count}):");
foreach (var rt in rtPage.ResourceTypes)
{
    Console.WriteLine($"  {rt.Id}");
}
System.Diagnostics.Debug.Assert(
    rtPage.ResourceTypes.Any(r => r.Id == "invoice"),
    "Expected 'invoice' in resource types");

// list distinct actions (at least "invoice.created" should be present now)
var actPage = await client.Audit.Actions.ListAsync(new ListActionsInput
{
    FilterResourceType = "invoice",
});
Console.WriteLine($"Actions for 'invoice' ({actPage.Actions.Count}):");
foreach (var a in actPage.Actions)
{
    Console.WriteLine($"  {a.Id}");
}
System.Diagnostics.Debug.Assert(
    actPage.Actions.Any(a => a.Id == "invoice.created"),
    "Expected 'invoice.created' in actions");

Console.WriteLine("Done!");
