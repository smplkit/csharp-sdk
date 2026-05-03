using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Logging;
using GenLogging = Smplkit.Internal.Generated.Logging;

namespace Smplkit.Management;

/// <summary>
/// Provides logger CRUD operations on the management plane. Owns the wire code
/// (HTTP request bodies, response mapping, generated-client invocation) so it
/// has no dependency on the runtime <see cref="Smplkit.Logging.LoggingClient"/>.
/// Accessible via <see cref="SmplManagementClient.Loggers"/>.
/// </summary>
public sealed class LoggersClient
{
    private readonly GenLogging.LoggingClient _genClient;

    internal LoggersClient(GeneratedClientFactory clients)
    {
        _genClient = clients.Logging;
    }

    /// <summary>
    /// Creates an unsaved logger. The id doubles as the display name; <c>managed</c>
    /// defaults to <c>true</c> (every logger created via the management API is by
    /// definition managed).
    /// </summary>
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

    /// <summary>Lists all loggers.</summary>
    public async Task<List<Logger>> ListAsync(CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_loggersAsync(cancellationToken: ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<Logger>();
        return response.Data.Select(r => MapLoggerResource(r)!).Where(l => l is not null).ToList();
    }

    /// <summary>Fetches a logger by id.</summary>
    /// <exception cref="NotFoundException">If no matching logger exists.</exception>
    public async Task<Logger> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_loggerAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLoggerResource(response.Data)
            ?? throw new NotFoundException($"Logger with id '{id}' not found");
    }

    /// <summary>Deletes a logger by id.</summary>
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

    /// <summary>
    /// Registers explicit logger sources with per-source service and environment overrides.
    /// </summary>
    public async Task RegisterAsync(IEnumerable<LoggerSource> sources, CancellationToken ct = default)
    {
        var items = sources.Select(s => new GenLogging.LoggerBulkItem
        {
            Id = s.Name,
            Level = s.Level?.ToWireString(),
            Resolved_level = s.ResolvedLevel?.ToWireString(),
            Service = s.Service,
            Environment = s.Environment,
        }).ToList();

        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Bulk_register_loggersAsync(
                new GenLogging.LoggerBulkRequest { Loggers = items }, ct)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Wire helpers — moved from runtime LoggingClient.
    // ------------------------------------------------------------------

    private Logger? MapLoggerResource(GenLogging.LoggerResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        LogLevel? level = null;
        if (attrs.Level is not null)
        {
            try { level = LogLevelExtensions.ParseLogLevel(attrs.Level); }
            catch { /* Unknown level */ }
        }

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

    private static GenLogging.LoggerResponse BuildLoggerRequestBody(Logger logger) =>
        new()
        {
            Data = new GenLogging.LoggerResource
            {
                Type = "logger",
                Id = logger.Id,
                Attributes = new GenLogging.Logger
                {
                    Name = logger.Name,
                    Level = logger.Level?.ToWireString(),
                    Group = logger.Group,
                    Managed = logger.Managed,
                    Environments = BuildEnvironmentsPayload(logger.Environments),
                },
            }
        };

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
}
