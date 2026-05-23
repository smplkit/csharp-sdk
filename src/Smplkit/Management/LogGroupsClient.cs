using Smplkit.Errors;
using Smplkit.Internal;
using Smplkit.Logging;
using GenLogging = Smplkit.Internal.Generated.Logging;

namespace Smplkit.Management;

/// <summary>
/// Provides log-group CRUD operations on the management plane. Owns the wire
/// code (HTTP request bodies, response mapping, generated-client invocation)
/// so it has no dependency on the runtime
/// <see cref="Smplkit.Logging.LoggingClient"/>. Accessible via
/// <see cref="SmplManagementClient.LogGroups"/>.
/// </summary>
public sealed class LogGroupsClient
{
    private readonly GenLogging.LoggingClient _genClient;

    internal LogGroupsClient(GeneratedClientFactory clients)
    {
        _genClient = clients.Logging;
    }

    /// <summary>Creates an unsaved log group.</summary>
    public LogGroup New(string id, string? name = null, string? group = null)
    {
        return new LogGroup(
            client: this,
            id: id,
            name: name ?? Helpers.KeyToDisplayName(id),
            level: null,
            group: group,
            environments: new Dictionary<string, Dictionary<string, object?>>(),
            createdAt: null,
            updatedAt: null);
    }

    /// <summary>Lists log groups. Returns one page; defaults to the server's first page.</summary>
    /// <param name="pageNumber">1-based page number; null lets the server default (1) apply.</param>
    /// <param name="pageSize">Items per page; null lets the server default (1000) apply.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<LogGroup>> ListAsync(
        int? pageNumber = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.List_log_groupsAsync(null, pageNumber, pageSize, null, ct)).ConfigureAwait(false);
        if (response.Data is null) return new List<LogGroup>();
        return response.Data.Select(r => MapLogGroupResource(r)!).Where(g => g is not null).ToList();
    }

    /// <summary>Fetches a log group by id.</summary>
    /// <exception cref="NotFoundException">If no matching group exists.</exception>
    public async Task<LogGroup> GetAsync(string id, CancellationToken ct = default)
    {
        var response = await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Get_log_groupAsync(id: id, cancellationToken: ct)).ConfigureAwait(false);
        return MapLogGroupResource(response.Data)
            ?? throw new NotFoundException($"LogGroup with id '{id}' not found");
    }

    /// <summary>Deletes a log group by id.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await ApiExceptionMapper.ExecuteAsync(
            () => _genClient.Delete_log_groupAsync(id, ct)).ConfigureAwait(false);
    }

    /// <summary>Internal: save a log group (create or update).</summary>
    internal async Task<LogGroup> SaveLogGroupInternalAsync(LogGroup logGroup, CancellationToken ct = default)
    {
        if (logGroup.CreatedAt is null)
        {
            var createBody = BuildLogGroupCreateRequestBody(logGroup);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Create_log_groupAsync(createBody, ct)).ConfigureAwait(false);
            return MapLogGroupResource(response.Data)
                ?? throw new ValidationException("Failed to create log group");
        }
        else
        {
            var groupId = logGroup.Id ?? throw new ValidationException("Cannot update a log group without an id");
            var updateBody = BuildLogGroupRequestBody(logGroup);
            var response = await ApiExceptionMapper.ExecuteAsync(
                () => _genClient.Update_log_groupAsync(groupId, updateBody, ct)).ConfigureAwait(false);
            return MapLogGroupResource(response.Data)
                ?? throw new ValidationException("Failed to update log group");
        }
    }

    private LogGroup? MapLogGroupResource(GenLogging.LogGroupResource? resource)
    {
        if (resource?.Attributes is null) return null;
        var attrs = resource.Attributes;

        LogLevel? level = attrs.Level is null
            ? null
            : LogLevelExtensions.TryParseLogLevel(attrs.Level.Value.ToString());

        var environments = LoggersClient.NormalizeEnvironments(attrs.Environments);

        return new LogGroup(
            client: this,
            id: resource.Id ?? string.Empty,
            name: attrs.Name ?? string.Empty,
            level: level,
            group: attrs.Parent_id,
            environments: environments,
            createdAt: attrs.Created_at?.DateTime,
            updatedAt: attrs.Updated_at?.DateTime);
    }

    private static GenLogging.LogGroupRequest BuildLogGroupRequestBody(LogGroup logGroup) =>
        new()
        {
            Data = new GenLogging.LogGroupResource
            {
                Type = "log_group",
                Id = logGroup.Id,
                Attributes = BuildLogGroupAttributes(logGroup),
            }
        };

    private static GenLogging.LogGroupCreateRequest BuildLogGroupCreateRequestBody(LogGroup logGroup) =>
        new()
        {
            Data = new GenLogging.LogGroupCreateResource
            {
                Type = "log_group",
                Id = logGroup.Id ?? throw new ValidationException("Cannot create a log group without an id"),
                Attributes = BuildLogGroupAttributes(logGroup),
            }
        };

    private static GenLogging.LogGroup BuildLogGroupAttributes(LogGroup logGroup) =>
        new()
        {
            Name = logGroup.Name,
            Level = logGroup.Level is null
                ? null
                : (GenLogging.LogLevel)System.Enum.Parse(typeof(GenLogging.LogLevel), logGroup.Level.Value.ToWireString()),
            Parent_id = logGroup.Group,
            Environments = LoggersClient.BuildEnvironmentsPayload(logGroup.Environments),
        };
}
