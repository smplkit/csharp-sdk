using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using GenLogging = Smplkit.Internal.Generated.Logging;

namespace Smplkit.Logging;

/// <summary>
/// Surface for <c>client.Logging.Loggers.*</c>.
/// </summary>
/// <remarks>
/// Logger CRUD plus the discovery buffer. The buffer is owned by the fused
/// <see cref="LoggingClient"/> and shared here so discovery (driven by
/// <see cref="LoggingClient.InstallAsync"/>) and explicit <see cref="RegisterAsync"/>
/// drain through one queue.
/// </remarks>
public sealed class LoggersClient
{
    private readonly GenLogging.LoggingClient _genClient;
    private readonly LoggerRegistrationBuffer _buffer;

    internal LoggersClient(GenLogging.LoggingClient genClient, LoggerRegistrationBuffer buffer)
    {
        _genClient = genClient;
        _buffer = buffer;
    }

    /// <summary>Buffer logger sources for registration; optionally flush immediately.</summary>
    /// <param name="sources">Logger sources to register.</param>
    /// <param name="flush">Whether to send the buffer immediately. Defaults to <c>true</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterAsync(IEnumerable<LoggerSource> sources, bool flush = true, CancellationToken ct = default)
    {
        foreach (var src in sources)
            _buffer.Add(
                NormalizeLoggerName(src.Name),
                src.Level?.ToWireString(),
                src.ResolvedLevel?.ToWireString(),
                src.Service,
                src.Environment);
        if (flush)
            await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drain the buffer and POST pending logger sources to the bulk endpoint.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var request = BuildLoggerBulkRequest(_buffer);
        if (request is null) return;
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Bulk_register_loggersAsync(request, ct)).ConfigureAwait(false);
    }

    /// <summary>Number of sources queued and awaiting flush.</summary>
    public int PendingCount => _buffer.PendingCount;

    /// <summary>Return a new unsaved <see cref="Logger"/>. Call <see cref="Logger.SaveAsync"/> to persist.</summary>
    /// <param name="id">The logger identifier (slug); doubles as the display name.</param>
    /// <param name="managed">Whether this logger is managed. Defaults to <c>true</c>.</param>
    public Logger New(string id, bool managed = true)
    {
        return new Logger(
            client: this,
            id: id,
            name: id,
            level: null,
            group: null,
            managed: managed,
            sources: new List<Dictionary<string, object?>>(),
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>List loggers for the authenticated account.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<Logger>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_loggersAsync(
                pagenumber: pageNumber,
                pagesize: pageSize,
                cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Logger>();
        return response.Data.Select(r => MapLoggerResource(r)!).Where(l => l is not null).ToList();
    }

    /// <summary>Fetch the editable <see cref="Logger"/> resource by id.</summary>
    /// <exception cref="NotFoundException">If no matching logger exists.</exception>
    public async Task<Logger> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_loggerAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLoggerResource(response.Data)
            ?? throw new NotFoundException($"Logger with id '{id}' not found");
    }

    /// <summary>Delete a logger by id.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_loggerAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a logger (PUT — upsert semantics).</summary>
    internal async Task<Logger> SaveLoggerInternalAsync(Logger logger, CancellationToken ct = default)
    {
        var loggerId = logger.Id ?? throw new ValidationException("Cannot save a logger without an id");

        var body = BuildLoggerRequestBody(logger);
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Update_loggerAsync(loggerId, body, ct)).ConfigureAwait(false);
        return MapLoggerResource(response.Data)
            ?? throw new ValidationException("Failed to save logger");
    }

    // ------------------------------------------------------------------
    // Wire helpers
    // ------------------------------------------------------------------

    private Logger? MapLoggerResource(GenLogging.LoggerResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        LogLevel? level = attrs.Level is null
            ? null
            : LogLevelExtensions.TryParseLogLevel(attrs.Level.Value.ToString());

        var sources = new List<Dictionary<string, object?>>();
        if (attrs.Sources is not null)
        {
            foreach (var s in attrs.Sources)
            {
                if (s is JsonElement je)
                    sources.Add(NormalizeJsonToDict(je));
            }
        }

        var environments = NormalizeEnvironments(attrs.Environments);

        return new Logger(
            client: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            level: level,
            group: attrs.Group,
            managed: attrs.Managed ?? false,
            sources: sources,
            environments: environments,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static GenLogging.LoggerRequest BuildLoggerRequestBody(Logger logger) =>
        new()
        {
            Data = new GenLogging.LoggerResource
            {
                Type = "logger",
                Id = logger.Id,
                Attributes = new GenLogging.Logger
                {
                    Name = logger.Name,
                    Level = logger.Level is null
                        ? null
                        : (GenLogging.LogLevel)System.Enum.Parse(typeof(GenLogging.LogLevel), logger.Level.Value.ToWireString()),
                    Group = logger.Group,
                    Managed = logger.Managed,
                    Environments = BuildEnvironmentsPayload(logger.EnvironmentsRaw),
                },
            }
        };

    /// <summary>Drain the logger-discovery buffer and build a JSON:API bulk request body.
    /// Returns <see langword="null"/> when there is nothing to flush.</summary>
    internal static GenLogging.LoggerBulkRequest? BuildLoggerBulkRequest(LoggerRegistrationBuffer buffer)
    {
        var batch = buffer.Drain();
        if (batch.Count == 0) return null;
        var items = batch.Select(e =>
        {
            var item = new GenLogging.LoggerBulkItem
            {
                Id = e.Id,
                Resolved_level = e.ResolvedLevel,
            };
            if (e.Level is not null) item.Level = e.Level;
            if (e.Service is not null) item.Service = e.Service;
            if (e.Environment is not null) item.Environment = e.Environment;
            return item;
        }).ToList();
        return new GenLogging.LoggerBulkRequest { Loggers = items };
    }

    internal static object? BuildEnvironmentsPayload(Dictionary<string, Dictionary<string, object?>> environments)
        => environments.Count == 0 ? null : (object?)environments;

    internal static Dictionary<string, Dictionary<string, object?>> NormalizeEnvironments(object? environments)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        if (environments is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in je.EnumerateObject())
                result[prop.Name] = NormalizeJsonToDict(prop.Value);
        }
        return result;
    }

    internal static Dictionary<string, object?> NormalizeJsonToDict(JsonElement je)
    {
        if (je.ValueKind != JsonValueKind.Object) return new Dictionary<string, object?>();
        var result = new Dictionary<string, object?>();
        foreach (var prop in je.EnumerateObject())
            result[prop.Name] = Smplkit.Config.Resolver.Normalize(prop.Value);
        return result;
    }

    /// <summary>
    /// Normalize a logger name: replace <c>/</c> and <c>:</c> with <c>.</c> and lowercase.
    /// Mirrors <c>smplkit.logging._normalize.normalize_logger_name</c>.
    /// </summary>
    internal static string NormalizeLoggerName(string name)
        => name.Replace('/', '.').Replace(':', '.').ToLowerInvariant();
}
