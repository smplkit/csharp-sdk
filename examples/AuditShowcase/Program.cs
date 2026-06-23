// Demonstrates the smplkit SDK for Smpl Audit.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key, provided via one of:
//         - SMPLKIT_API_KEY environment variable
//         - ~/.smplkit configuration file (see SDK docs)
//
// Usage:
//     dotnet run --project examples/AuditShowcase

using System.Diagnostics;
using Smplkit;
using Smplkit.Audit;
using Smplkit.Errors;
using HttpMethod = Smplkit.Audit.HttpMethod;

// JSON Logic filter — only forward `invoice.*` actions. Events that don't
// match the filter aren't forwarded (and produce no delivery record).
// See https://jsonlogic.com for the full operator reference.
var invoiceFilter = new Dictionary<string, object?>
{
    ["in"] = new object[] { "invoice.", new Dictionary<string, object?> { ["var"] = "action" } },
};

// JSONata template — reshape the event payload before POSTing to the
// destination. This example flattens the event into a compact SIEM-style
// record. See https://jsonata.org for the full language reference.
const string SiemTransform = """
    {
        "event": action,
        "subject": resource_type & ":" & resource_id,
        "ts": occurred_at,
        "actor": actor_label
    }
    """;

// create the client
using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});
var audit = client.Audit;
var someResourceId = "showcase-" + Guid.NewGuid().ToString("N")[..8];

// ----- Events: record / list / get --------------------------------

// record an event
audit.Events.Record(new CreateEventInput
{
    ActorId = "billing-bot:42",
    ActorLabel = "finance@example.com",
    ActorType = "USER",
    Category = "billing",
    Data = new Dictionary<string, object?>
    {
        ["snapshot"] = new Dictionary<string, object?> { ["total_cents"] = 4900, ["currency"] = "USD" },
        ["request_id"] = "req-abc",
    },
    EventType = "invoice.created",
    OccurredAt = DateTimeOffset.UtcNow,
    ResourceId = someResourceId,
    ResourceType = "invoice",
});
// force the event to be posted (normally happens automatically, in the
// background, but we force it here so the read-backs below find it)
await audit.Events.FlushAsync(TimeSpan.FromSeconds(2));
Console.WriteLine($"Recorded event for invoice {someResourceId}");

// list events
var page = await audit.Events.ListAsync(new ListEventsInput
{
    ResourceType = "invoice",
    ResourceId = someResourceId,
});
Debug.Assert(page.Events.Any(e => e.ResourceId == someResourceId));
var recordedEventId = page.Events[0].Id;
Console.WriteLine($"Listed {page.Events.Count} event(s) for invoice {someResourceId}");

// fetch an event
var ev = await audit.Events.GetAsync(recordedEventId);
Debug.Assert(ev.Id == recordedEventId);
Debug.Assert(ev.ResourceId == someResourceId);
Debug.Assert(ev.EventType == "invoice.created");
Debug.Assert(ev.ActorId == "billing-bot:42");
Debug.Assert(ev.ActorLabel == "finance@example.com");
Debug.Assert(ev.Category == "billing");
Console.WriteLine($"Fetched event {ev.Id}: {ev.EventType} by {ev.ActorLabel} in {ev.Environment}");

// ----- Discovery: distinct resource_types / event_types / categories

var resourceTypes = await audit.ResourceTypes.ListAsync();
Debug.Assert(resourceTypes.ResourceTypes.Any(rt => rt.Id == "invoice"));
Console.WriteLine($"Observed resource types: [{string.Join(", ", resourceTypes.ResourceTypes.Select(rt => rt.Id))}]");

var eventTypes = await audit.EventTypes.ListAsync();
Debug.Assert(eventTypes.EventTypes.Any(et => et.Id == "invoice.created"));
Console.WriteLine($"Observed event types: [{string.Join(", ", eventTypes.EventTypes.Select(et => et.Id))}]");

var categories = await audit.Categories.ListAsync();
Debug.Assert(categories.Categories.Any(c => c.Id == "billing"));
Console.WriteLine($"Observed categories: [{string.Join(", ", categories.Categories.Select(c => c.Id))}]");

// ----- Forwarders: SIEM streaming CRUD ----------------------------

var forwarderId = "showcase-" + Guid.NewGuid().ToString("N")[..6];

try
{
    // create a forwarder (disabled by default)
    var forwarder = audit.Forwarders.New(
        forwarderId,
        forwarderType: ForwarderType.Http,
        configuration: new HttpConfiguration
        {
            Headers = new Dictionary<string, string> { ["X-Showcase"] = "ok" },
            Method = HttpMethod.Post,
            Url = "https://example.com",
        },
        filter: invoiceFilter,
        transform: SiemTransform,
        transformType: TransformType.Jsonata);
    await forwarder.SaveAsync();
    Console.WriteLine($"Created forwarder: {forwarder.Name} (id={forwarder.Id})");

    // list forwarders
    var listed = await audit.Forwarders.ListAsync();
    Debug.Assert(listed.Forwarders.Any(f => f.Id == forwarder.Id));
    Console.WriteLine($"Account has {listed.Forwarders.Count} forwarder(s)");

    // get a forwarder
    forwarder = await client.Audit.Forwarders.GetAsync(forwarderId);
    Console.WriteLine($"Fetched forwarder: {forwarder.Name} (id={forwarder.Id})");
    Debug.Assert(forwarder.Id == forwarderId);

    // configure where to forward events in production (only the leaves you set)
    forwarder.Environment("production").Url = "https://httpbin.org/post";
    forwarder.Environment("production").SetHeader("X-Showcase", "ok");
    await forwarder.SaveAsync();
    Debug.Assert(forwarder.Environments["production"].Url == "https://httpbin.org/post");
    Console.WriteLine($"Updated forwarder: {forwarder.Name}");

    // start forwarding events in production
    forwarder.Environment("production").Enabled = true;
    await forwarder.SaveAsync();
    Console.WriteLine($"Enabled forwarder {forwarder.Name} (id={forwarder.Id}) "
        + "to start forwarding events in production");

    // delete a forwarder
    await forwarder.DeleteAsync();
    var remaining = await audit.Forwarders.ListAsync();
    Debug.Assert(remaining.Forwarders.All(f => f.Id != forwarderId));
    Console.WriteLine($"Deleted forwarder: {forwarder.Name}");
}
finally
{
    // tear-down: never leave the showcase forwarder behind, even on failure
    try { await audit.Forwarders.DeleteAsync(forwarderId); }
    catch (NotFoundException) { }
}

Console.WriteLine("Done!");
