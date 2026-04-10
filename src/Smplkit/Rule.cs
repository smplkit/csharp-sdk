namespace Smplkit;

/// <summary>
/// Builder for targeting rules.
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
    /// Tags this rule with an environment key.
    /// </summary>
    /// <param name="envKey">The environment key.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule Environment(string envKey)
    {
        _environment = envKey;
        return this;
    }

    /// <summary>
    /// Adds a condition. Multiple conditions are combined with AND.
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
    /// Sets the value returned when this rule matches.
    /// </summary>
    /// <param name="value">The value to serve.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule Serve(object? value)
    {
        _value = value;
        return this;
    }

    /// <summary>
    /// Builds and returns the rule as a dictionary.
    /// </summary>
    /// <returns>A dictionary representing the rule.</returns>
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
