// Demonstrates the smplkit management SDK for Smpl Audit.
//
// Covers: SIEM forwarder create / get / list / update / delete.
//
// JSON Logic (https://jsonlogic.com) filters route events selectively to your
// SIEM. JSONata (https://jsonata.org) transforms reshape each event payload
// before delivery.
//
// Prerequisites:
//     - dotnet add package Smplkit.Sdk
//     - A valid smplkit API key (Pro tier required for forwarders)
//
// Usage:
//     dotnet run --project examples/AuditManagementShowcase

using Smplkit;
using Smplkit.Audit;

// create the management client
using var mgmt = new SmplManagementClient();

var name = "showcase " + Guid.NewGuid().ToString("N")[..8];

// create a forwarder
// - filter: JSON Logic expression — only forward invoice events
// - transform: JSONata expression — reshape the payload before delivery
var fwd = await mgmt.Audit.Forwarders.CreateAsync(new CreateForwarderInput
{
    Name = name,
    ForwarderType = ForwarderType.Http,
    Http = new ForwarderHttp
    {
        Url = "https://httpbin.org/post",
        Headers = new List<HttpHeader> { new("X-Showcase", "ok") },
    },
    Filter = new Dictionary<string, object?> { ["=="] = new object[] { new Dictionary<string, object?> { ["var"] = "resource_type" }, "invoice" } },
    Transform = "$",
});
Console.WriteLine($"Created forwarder: id={fwd.Id}  slug={fwd.Slug}  type={fwd.ForwarderType}");
System.Diagnostics.Debug.Assert(fwd.Name == name);
System.Diagnostics.Debug.Assert(fwd.ForwarderType == ForwarderType.Http);
System.Diagnostics.Debug.Assert(fwd.Enabled);

try
{
    // get the forwarder by id
    var fetched = await mgmt.Audit.Forwarders.GetAsync(fwd.Id);
    Console.WriteLine($"Fetched: id={fetched.Id}  slug={fetched.Slug}");
    System.Diagnostics.Debug.Assert(fetched.Id == fwd.Id);
    System.Diagnostics.Debug.Assert(fetched.Name == fwd.Name);

    // list forwarders — the one we just created should appear
    var page = await mgmt.Audit.Forwarders.ListAsync(new ListForwardersInput
    {
        ForwarderType = ForwarderType.Http,
        Enabled = true,
        PageSize = 50,
    });
    Console.WriteLine($"Listed {page.Forwarders.Count} forwarder(s) (cursor={page.NextCursor ?? "none"})");
    System.Diagnostics.Debug.Assert(page.Forwarders.Any(f => f.Id == fwd.Id));

    // update the forwarder (rename and add a header)
    var updated = await mgmt.Audit.Forwarders.UpdateAsync(fwd.Id, new CreateForwarderInput
    {
        Name = name + " updated",
        ForwarderType = ForwarderType.Http,
        Http = new ForwarderHttp
        {
            Url = "https://httpbin.org/post",
            Headers = new List<HttpHeader>
            {
                new("X-Showcase", "ok"),
                new("X-Updated", "true"),
            },
        },
    });
    Console.WriteLine($"Updated: name={updated.Name}  headers={updated.Http.Headers.Count}");
    System.Diagnostics.Debug.Assert(updated.Http.Headers.Count == 2);
}
finally
{
    // delete the forwarder
    await mgmt.Audit.Forwarders.DeleteAsync(fwd.Id);
    Console.WriteLine($"Deleted forwarder: {fwd.Id}");
}

Console.WriteLine("Done!");
