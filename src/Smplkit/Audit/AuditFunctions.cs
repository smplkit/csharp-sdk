using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Audit;

/// <summary>
/// Server-side audit functions surface — accessed via
/// <see cref="AuditClient.Functions"/>.
///
/// <para>Today the only function is <c>test_forwarder/execute</c>, the
/// console's "try it out" proxy. The audit service applies its SSRF
/// guard before resolving — private/loopback/link-local addresses
/// (incl. the EC2 IMDS at <c>169.254.169.254</c>) and disallowed ports
/// are rejected with <c>Succeeded=false</c> and a descriptive
/// <c>Error</c>.</para>
/// </summary>
public sealed class AuditFunctions
{
    private readonly GenAudit.AuditClient _gen;

    internal AuditFunctions(GenAudit.AuditClient gen)
    {
        _gen = gen;
    }

    /// <summary>Server-side proxy for a test HTTP request to a destination.</summary>
    public async Task<TestForwarderResult> ExecuteTestForwarderAsync(
        TestForwarderInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var body = new GenAudit.TestForwarderRequest
        {
            Url = input.Url,
            Method = input.Method,
            Success_status = input.SuccessStatus,
        };
        if (input.Body != null) body.Body = input.Body;
        if (input.TimeoutMs.HasValue) body.Timeout_ms = input.TimeoutMs.Value;
        if (input.Headers != null && input.Headers.Count > 0)
        {
            body.Headers = input.Headers
                .Select(h => new GenAudit.HttpHeader { Name = h.Name, Value = h.Value })
                .ToList();
        }
        var resp = await _gen.Execute_test_forwarderAsync(body, ct).ConfigureAwait(false);
        var headers = resp.Response_headers != null
            ? new Dictionary<string, string>(resp.Response_headers)
            : new Dictionary<string, string>();
        return new TestForwarderResult(
            resp.Succeeded,
            resp.Response_status,
            headers,
            resp.Response_body ?? string.Empty,
            resp.Latency_ms,
            resp.Error);
    }
}
