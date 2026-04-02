using System.Text.Json.Serialization;

namespace Smplkit.Flags;

/// <summary>
/// The value type of a flag.
/// </summary>
public enum FlagType
{
    /// <summary>Boolean flag type.</summary>
    Boolean,

    /// <summary>String flag type.</summary>
    String,

    /// <summary>Numeric flag type.</summary>
    Numeric,

    /// <summary>JSON flag type.</summary>
    Json,
}

/// <summary>
/// Extension methods for <see cref="FlagType"/>.
/// </summary>
public static class FlagTypeExtensions
{
    /// <summary>
    /// Returns the wire-format string for a <see cref="FlagType"/>.
    /// </summary>
    public static string ToWireString(this FlagType flagType) => flagType switch
    {
        FlagType.Boolean => "BOOLEAN",
        FlagType.String => "STRING",
        FlagType.Numeric => "NUMERIC",
        FlagType.Json => "JSON",
        _ => throw new ArgumentOutOfRangeException(nameof(flagType)),
    };

    /// <summary>
    /// Parses a wire-format string to a <see cref="FlagType"/>.
    /// </summary>
    public static FlagType ParseFlagType(string wireString) => wireString switch
    {
        "BOOLEAN" => FlagType.Boolean,
        "STRING" => FlagType.String,
        "NUMERIC" => FlagType.Numeric,
        "JSON" => FlagType.Json,
        _ => throw new ArgumentException($"Unknown flag type: {wireString}", nameof(wireString)),
    };
}

/// <summary>
/// A typed evaluation context entity.
/// Represents a single entity (user, account, device, etc.) in the evaluation context.
/// </summary>
/// <example>
/// <code>
/// new Context("user", "user-123", new Dictionary&lt;string, object?&gt; { ["plan"] = "enterprise" })
/// new Context("user", "user-123", new Dictionary&lt;string, object?&gt; { ["plan"] = "enterprise" }, name: "Alice")
/// </code>
/// </example>
public sealed class Context
{
    /// <summary>Gets the context type (e.g., "user", "account", "device").</summary>
    public string Type { get; }

    /// <summary>Gets the entity identifier (e.g., "user-123", "acme-corp").</summary>
    public string Key { get; }

    /// <summary>Gets the optional display name.</summary>
    public string? Name { get; }

    /// <summary>Gets the context attributes that JSON Logic rules target.</summary>
    public Dictionary<string, object?> Attributes { get; }

    /// <summary>
    /// Initializes a new <see cref="Context"/> with the specified type, key, and optional attributes.
    /// </summary>
    /// <param name="type">The context type (e.g., "user", "account").</param>
    /// <param name="key">The entity identifier.</param>
    /// <param name="attributes">Optional attribute dictionary.</param>
    /// <param name="name">Optional display name.</param>
    public Context(
        string type,
        string key,
        Dictionary<string, object?>? attributes = null,
        string? name = null)
    {
        Type = type;
        Key = key;
        Name = name;
        Attributes = attributes ?? new Dictionary<string, object?>();
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Context(Type={Type}, Key={Key}, Name={Name}, Attributes=[{string.Join(", ", Attributes.Keys)}])";
}

/// <summary>
/// Fluent builder for JSON Logic rule dicts.
/// </summary>
/// <example>
/// <code>
/// new Rule("Enable for enterprise users")
///     .When("user.plan", "==", "enterprise")
///     .Serve(true)
///     .Build();
/// </code>
/// </example>
public sealed class Rule
{
    private readonly string _description;
    private readonly List<Dictionary<string, object?>> _conditions = new();
    private object? _value;
    private string? _environment;

    /// <summary>
    /// Initializes a new <see cref="Rule"/> with the specified description.
    /// </summary>
    /// <param name="description">Human-readable description of what this rule does.</param>
    public Rule(string description)
    {
        _description = description;
    }

    /// <summary>
    /// Tag this rule with an environment key (used by <c>AddRuleAsync</c>).
    /// </summary>
    /// <param name="envKey">The environment key.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule Environment(string envKey)
    {
        _environment = envKey;
        return this;
    }

    /// <summary>
    /// Add a condition. Multiple calls are AND'd.
    /// </summary>
    /// <param name="var">The variable path (e.g., "user.plan").</param>
    /// <param name="op">The operator (==, !=, &gt;, &lt;, &gt;=, &lt;=, in, contains).</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule When(string var, string op, object? value)
    {
        if (op == "contains")
        {
            // JSON Logic "in" with reversed operands: value in var
            _conditions.Add(new Dictionary<string, object?>
            {
                ["in"] = new object?[] { value, new Dictionary<string, object?> { ["var"] = var } }
            });
        }
        else
        {
            _conditions.Add(new Dictionary<string, object?>
            {
                [op] = new object?[] { new Dictionary<string, object?> { ["var"] = var }, value }
            });
        }
        return this;
    }

    /// <summary>
    /// Set the value returned when this rule matches.
    /// </summary>
    /// <param name="value">The value to serve.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule Serve(object? value)
    {
        _value = value;
        return this;
    }

    /// <summary>
    /// Finalize and return the rule as a plain dictionary.
    /// </summary>
    /// <returns>A dictionary representing the built rule.</returns>
    public Dictionary<string, object?> Build()
    {
        object? logic;
        if (_conditions.Count == 1)
            logic = _conditions[0];
        else if (_conditions.Count > 1)
            logic = new Dictionary<string, object?> { ["and"] = _conditions.ToArray() };
        else
            logic = new Dictionary<string, object?>();

        var result = new Dictionary<string, object?>
        {
            ["description"] = _description,
            ["logic"] = logic,
            ["value"] = _value,
        };

        if (_environment is not null)
            result["environment"] = _environment;

        return result;
    }
}
