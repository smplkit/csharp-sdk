namespace Smplkit.Config;

/// <summary>The type of a <see cref="ConfigItem"/> value.</summary>
public enum ItemType
{
    /// <summary>String value.</summary>
    String,

    /// <summary>Numeric value.</summary>
    Number,

    /// <summary>Boolean value.</summary>
    Boolean,

    /// <summary>JSON value.</summary>
    Json,
}

/// <summary>Extension methods for <see cref="ItemType"/>.</summary>
public static class ItemTypeExtensions
{
    /// <summary>Returns the wire-format string ("STRING", "NUMBER", etc.).</summary>
    public static string ToWireString(this ItemType type) => type switch
    {
        ItemType.String => "STRING",
        ItemType.Number => "NUMBER",
        ItemType.Boolean => "BOOLEAN",
        ItemType.Json => "JSON",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Parses a wire-format string. Unknown values default to <see cref="ItemType.Json"/>.</summary>
    public static ItemType Parse(string wire) => wire switch
    {
        "STRING" => ItemType.String,
        "NUMBER" => ItemType.Number,
        "BOOLEAN" => ItemType.Boolean,
        "JSON" => ItemType.Json,
        _ => ItemType.Json,
    };
}

/// <summary>A single typed item in a <see cref="Config"/>.</summary>
public sealed class ConfigItem
{
    /// <summary>The item name (key).</summary>
    public string Name { get; }

    /// <summary>The item value.</summary>
    public object? Value { get; }

    /// <summary>The item type.</summary>
    public ItemType Type { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }

    /// <summary>Initializes a new <see cref="ConfigItem"/>.</summary>
    public ConfigItem(string name, object? value, ItemType type, string? description = null)
    {
        Name = name;
        Value = value;
        Type = type;
        Description = description;
    }

    /// <inheritdoc />
    public override string ToString() => $"ConfigItem(Name={Name}, Value={Value}, Type={Type})";
}

/// <summary>
/// Per-environment value overrides for a <see cref="Config"/>.
/// </summary>
/// <remarks>
/// Read-only inspection container. Mutation is via <see cref="Config"/>'s setters
/// with <c>environment</c> argument (e.g., <see cref="Config.SetString"/>).
/// </remarks>
public sealed class ConfigEnvironment
{
    private readonly Dictionary<string, Dictionary<string, object?>> _valuesRaw;

    internal ConfigEnvironment(Dictionary<string, Dictionary<string, object?>>? raw = null)
    {
        _valuesRaw = raw ?? new Dictionary<string, Dictionary<string, object?>>();
    }

    /// <summary>
    /// Returns overrides as a plain dictionary <c>{key: rawValue}</c>.
    /// Returns a defensive copy — mutating it has no effect on the underlying state.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values
    {
        get
        {
            var result = new Dictionary<string, object?>(_valuesRaw.Count);
            foreach (var (k, v) in _valuesRaw)
                result[k] = v.TryGetValue("value", out var val) ? val : v;
            return result;
        }
    }

    /// <summary>
    /// Returns the full typed overrides <c>{key: {value, type, description}}</c>.
    /// Returns a deep defensive copy.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ValuesRaw
    {
        get
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(_valuesRaw.Count);
            foreach (var (k, v) in _valuesRaw)
                result[k] = new Dictionary<string, object?>(v);
            return result;
        }
    }

    internal Dictionary<string, Dictionary<string, object?>> Raw => _valuesRaw;

    /// <inheritdoc />
    public override string ToString() => $"ConfigEnvironment({_valuesRaw.Count} values)";
}
