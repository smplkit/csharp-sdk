namespace Smplkit;

/// <summary>
/// Internal sink that <see cref="Smplkit.Context"/> uses to persist itself to the server.
/// Implemented by <see cref="Smplkit.Platform.ContextsClient"/>.
/// </summary>
internal interface IContextSink
{
    Task<Smplkit.Context> SaveContextAsync(Smplkit.Context ctx, CancellationToken ct);
    Task DeleteContextAsync(string id, CancellationToken ct);
}
