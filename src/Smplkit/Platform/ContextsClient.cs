using System.Text.Json;
using Smplkit.Internal;
using ContextRegistrationBuffer = Smplkit.Flags.ContextRegistrationBuffer;
using GenApp = Smplkit.Internal.Generated.App;

namespace Smplkit.Platform;

/// <summary>
/// Context registration + read/delete (<c>client.Platform.Contexts</c>).
/// </summary>
public sealed class ContextsClient : IContextSink
{
    private readonly GenApp.AppClient _appClient;
    private readonly ContextRegistrationBuffer _buffer;

    internal ContextsClient(GenApp.AppClient appClient, ContextRegistrationBuffer buffer)
    {
        _appClient = appClient;
        _buffer = buffer;
    }

    /// <summary>Buffer a single context for registration; optionally flush immediately.</summary>
    public Task RegisterAsync(Smplkit.Context context, bool flush = false, CancellationToken ct = default)
        => RegisterAsync(new[] { context }, flush, ct);

    /// <summary>Buffer contexts for registration; optionally flush immediately.</summary>
    public async Task RegisterAsync(IEnumerable<Smplkit.Context> contexts, bool flush = false, CancellationToken ct = default)
    {
        _buffer.Observe(contexts);
        if (flush)
            await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Number of observations queued and awaiting flush.</summary>
    public int PendingCount => _buffer.PendingCount;

    /// <summary>Send any pending observations to the server.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var batch = _buffer.Drain();
        if (batch.Count == 0) return;

        var items = batch.Select(b =>
        {
            var composite = b.TryGetValue("id", out var idVal) && idVal is string idStr ? idStr : "";
            var colonIdx = composite.IndexOf(':');
            return new GenApp.ContextBulkItem
            {
                Type = colonIdx >= 0 ? composite[..colonIdx] : composite,
                Key = colonIdx >= 0 ? composite[(colonIdx + 1)..] : "",
                Attributes = b.TryGetValue("attributes", out var attrs) ? attrs ?? new object() : new object(),
            };
        }).ToList();

        await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Bulk_register_contextsAsync(
                new GenApp.ContextBulkRegister { Contexts = items }, ct)).ConfigureAwait(false);
    }

    /// <summary>List all contexts of a given type.</summary>
    /// <param name="type">Context type filter (required).</param>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Smplkit.Context>> ListAsync(
        string type,
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.List_contextsAsync(type, null, null, pageNumber, pageSize, null, ct)).ConfigureAwait(false);
        return resp.Data.Select(MapResource).ToList();
    }

    /// <summary>Fetches a context by composite <c>"{type}:{key}"</c> id.</summary>
    public async Task<Smplkit.Context> GetAsync(string id, CancellationToken ct = default)
    {
        var resp = await ApiExceptionMapper.ExecuteAsync(
            () => _appClient.Get_contextAsync(id, ct)).ConfigureAwait(false);
        return MapResource(resp.Data);
    }

    /// <summary>Fetches a context by type and key.</summary>
    public Task<Smplkit.Context> GetAsync(string type, string key, CancellationToken ct = default)
        => GetAsync($"{type}:{key}", ct);

    /// <summary>Deletes a context by composite <c>"{type}:{key}"</c> id.</summary>
    public Task DeleteAsync(string id, CancellationToken ct = default)
        => ApiExceptionMapper.ExecuteAsync(() => _appClient.Delete_contextAsync(id, ct));

    /// <summary>Deletes a context by type and key.</summary>
    public Task DeleteAsync(string type, string key, CancellationToken ct = default)
        => DeleteAsync($"{type}:{key}", ct);

    Task<Smplkit.Context> IContextSink.SaveContextAsync(Smplkit.Context ctx, CancellationToken ct)
        => SaveContextInternalAsync(ctx, ct);

    Task IContextSink.DeleteContextAsync(string id, CancellationToken ct) => DeleteAsync(id, ct);

    private async Task<Smplkit.Context> SaveContextInternalAsync(Smplkit.Context ctx, CancellationToken ct)
    {
        await RegisterAsync(new[] { ctx }, flush: true, ct).ConfigureAwait(false);
        return await GetAsync(ctx.Id, ct).ConfigureAwait(false);
    }

    private Smplkit.Context MapResource(GenApp.ContextResource r)
    {
        var composite = r.Id ?? "";
        var colonIdx = composite.IndexOf(':');
        var ctxType = colonIdx >= 0 ? composite[..colonIdx] : composite;
        var ctxKey = colonIdx >= 0 ? composite[(colonIdx + 1)..] : "";

        var attrs = r.Attributes;
        var attrDict = ParseAttributeDict(attrs.Attributes);

        return new Smplkit.Context(
            sink: this,
            type: ctxType,
            key: ctxKey,
            attributes: attrDict,
            name: attrs.Name,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static Dictionary<string, object?> ParseAttributeDict(object? raw)
    {
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
            return je.EnumerateObject().ToDictionary(p => p.Name, p => Smplkit.Config.Resolver.Normalize(p.Value));
        return new Dictionary<string, object?>();
    }
}
