// Demonstrates the smplkit runtime SDK for Smpl Audit.
//
// Audit is a fire-and-forget event-recording surface. Create enqueues
// the event onto an in-memory bounded buffer and returns immediately;
// the buffer worker retries with exponential backoff on transient
// failures and drops oldest under back-pressure (ADR-047 §2.6).
// Reads (GetAsync, ListAsync) are async and synchronous on the wire.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key
//
// Usage:
//     dotnet run --project examples/AuditRuntimeShowcase

using System.Diagnostics;
using Smplkit;
using Smplkit.Audit;

using var client = new SmplClient(new SmplClientOptions
{
    Environment = "production",
    Service = "showcase-service",
});

// unique resource id so we can find back exactly the events this
// showcase wrote, regardless of what other history exists.
var resourceId = "showcase-" + Guid.NewGuid().ToString("N").Substring(0, 8);

// 1) fire-and-forget Create — returns immediately. The actual POST
//    happens on the buffer worker. Customer events must NOT use a
//    ResourceType beginning with "smpl." — that namespace is reserved
//    for smplkit-emitted events; the server returns 403.
client.Audit.Events.Create(new CreateEventInput
{
    Action = "invoice.created",
    ResourceType = "invoice",
    ResourceId = resourceId,
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

// 2) caller-supplied idempotency key — replaying with the same key
//    returns the original event (server dedupes on
//    account_id + idempotency_key).
var idempotencyKey = "showcase-" + Guid.NewGuid();
for (var i = 0; i < 2; i++)
{
    client.Audit.Events.Create(new CreateEventInput
    {
        Action = "invoice.updated",
        ResourceType = "invoice",
        ResourceId = resourceId,
        Snapshot = new Dictionary<string, object?> { ["total_cents"] = 5400 },
        IdempotencyKey = idempotencyKey,
    });
}

// 3) Flush — block until the in-memory buffer drains so that the
//    events we just wrote are durable before we read them.
await client.Audit.Events.FlushAsync(TimeSpan.FromSeconds(5));

// 4) ListAsync — server-side filters per ADR-047 §4. Cursor
//    pagination via PageSize / PageAfter; page.NextCursor is non-null
//    when more pages exist.
var page = await client.Audit.Events.ListAsync(new ListEventsInput
{
    ResourceType = "invoice",
    ResourceId = resourceId,
    PageSize = 10,
});

Console.WriteLine($"Found {page.Events.Count} events for {resourceId}:");
foreach (var ev in page.Events)
{
    Console.WriteLine($"  {ev.Action}  id={ev.Id}  actor={ev.ActorType}");
}

// idempotency dedupe check — 3 creates (1 distinct + 2 with the same
// idempotency key) so we expect exactly 2 events.
Debug.Assert(
    page.Events.Count == 2,
    $"Expected 2 events (idempotency dedup), got {page.Events.Count}");

// 5) GetAsync — read a single event by id.
var first = await client.Audit.Events.GetAsync(page.Events[0].Id);
Console.WriteLine($"Round-tripped: {first.Action} at {first.OccurredAt}");

Console.WriteLine("Done!");
