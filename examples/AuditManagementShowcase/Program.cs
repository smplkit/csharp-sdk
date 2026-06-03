// Demonstrates the smplkit management SDK for Smpl Audit.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key, provided via one of:
//         - SMPLKIT_API_KEY environment variable
//         - ~/.smplkit configuration file (see SDK docs)
//
// Usage:
//     dotnet run --project examples/AuditManagementShowcase

using System.Diagnostics;
using Smplkit;
using Smplkit.Audit;
using HttpMethod = Smplkit.Audit.HttpMethod;


// JSON Logic filter — only forward `invoice.*` event types.
// Events that don't match the filter aren't forwarded (and produce no delivery record).
// See https://jsonlogic.com for the full operator reference.
var invoiceFilter = new Dictionary<string, object?>
{
    ["in"] = new object[] { "invoice.", new Dictionary<string, object?> { ["var"] = "event_type" } },
};

// JSONata template — reshape the event payload before POSTing to the
// destination. This example flattens the event into a compact SIEM-style
// record. See https://jsonata.org for the full language reference.
const string SiemTransform = """
    {
        "event": event_type,
        "subject": resource_type & ":" & resource_id,
        "ts": occurred_at,
        "actor": actor_label
    }
    """;


// create the client
using var manage = new SmplManagementClient();
var forwarderName = $"showcase-{Guid.NewGuid().ToString("N")[..6]}";

// create a new forwarder
var forwarder = manage.Audit.Forwarders.New(
    key: forwarderName,
    name: forwarderName,
    forwarderType: ForwarderType.Http,
    configuration: new HttpConfiguration
    {
        Method = HttpMethod.Post,
        Url = "https://httpbin.org/post",
        Headers = new List<HttpHeader> { new("X-Showcase", "ok") },
    },
    filter: invoiceFilter,
    transform: SiemTransform,
    transformType: TransformType.Jsonata);
await forwarder.SaveAsync();
Console.WriteLine($"Created forwarder: {forwarder.Name} (id={forwarder.Id})");

// list forwarders
var listed = await manage.Audit.Forwarders.ListAsync();
Debug.Assert(listed.Forwarders.Any(f => f.Id == forwarder.Id));
Console.WriteLine($"Account has {listed.Forwarders.Count} forwarder(s)");

// get a forwarder
var fetched = await manage.Audit.Forwarders.GetAsync(forwarder.Id!);
Debug.Assert(fetched.Id == forwarder.Id);
Debug.Assert(fetched.Enabled == true);
Console.WriteLine($"Fetched forwarder: {fetched.Name}");

// update a forwarder
fetched.Enabled = false;
await fetched.SaveAsync();
Debug.Assert(fetched.Enabled == false);
Console.WriteLine($"Disabled forwarder: {fetched.Name} (enabled={fetched.Enabled})");

// delete a forwarder
await fetched.DeleteAsync();
var remaining = await manage.Audit.Forwarders.ListAsync();
Debug.Assert(remaining.Forwarders.All(f => f.Id != fetched.Id));
Console.WriteLine($"Deleted forwarder: {fetched.Name}");

Console.WriteLine("Done!");
