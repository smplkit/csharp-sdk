namespace Smplkit;

/// <summary>
/// Operators accepted by <see cref="Rule.When(string, Op, object?)"/>.
/// </summary>
/// <remarks>
/// Prefer these typed members over raw operator strings so the compiler can
/// validate the call. The raw-string overload
/// (<see cref="Rule.When(string, string, object?)"/>) remains available for any
/// operator not represented here.
/// </remarks>
public enum Op
{
    /// <summary>Equality (<c>==</c>).</summary>
    Eq,
    /// <summary>Inequality (<c>!=</c>).</summary>
    Neq,
    /// <summary>Greater-than (<c>&gt;</c>).</summary>
    Gt,
    /// <summary>Greater-than-or-equal (<c>&gt;=</c>).</summary>
    Gte,
    /// <summary>Less-than (<c>&lt;</c>).</summary>
    Lt,
    /// <summary>Less-than-or-equal (<c>&lt;=</c>).</summary>
    Lte,
    /// <summary>Membership — the variable's value is one of the supplied collection.</summary>
    In,
    /// <summary>Containment — the supplied value is an element of the variable's collection.</summary>
    Contains,
}

/// <summary>
/// Builder for targeting rules.
/// </summary>
/// <remarks>
/// Multiple <see cref="When(string, Op, object?)"/> conditions are combined with
/// AND. <see cref="When(System.Collections.Generic.IDictionary{string, object?})"/>
/// accepts a raw JSON Logic expression for predicates the convenience form can't
/// express (OR, nested AND/OR, <c>if</c>, …; see <see href="https://jsonlogic.com/"/>).
/// Supplying <c>environment</c> — either to the constructor or via
/// <see cref="Environment(string)"/> — keeps the target environment unambiguous when
/// the built rule is passed to <c>Flag.AddRule</c>.
/// </remarks>
/// <example>
/// <code>
/// new Rule("Enable for enterprise users", "production")
///     .When("user.plan", Op.Eq, "enterprise")
///     .Serve(true)
///     .Build();
/// </code>
/// </example>
public sealed class Rule
{
    // Wire operators keyed by typed Op, mirroring the dictionary-based mapping
    // the audit enums use. The raw-string When overload still owns the
    // "contains" → reversed-"in" rewrite, so this map carries the literal
    // JSON Logic operator for each member.
    private static readonly IReadOnlyDictionary<Op, string> OpWire = new Dictionary<Op, string>
    {
        [Op.Eq] = "==",
        [Op.Neq] = "!=",
        [Op.Gt] = ">",
        [Op.Gte] = ">=",
        [Op.Lt] = "<",
        [Op.Lte] = "<=",
        [Op.In] = "in",
        [Op.Contains] = "contains",
    };

    private readonly string _description;
    private readonly List<Dictionary<string, object?>> _conditions = new();
    private object? _value;
    private string? _environment;

    /// <summary>
    /// Initializes a new <see cref="Rule"/> with the specified description.
    /// </summary>
    /// <remarks>
    /// Use this overload only when the environment is applied separately via
    /// <see cref="Environment(string)"/>. Prefer
    /// <see cref="Rule(string, string)"/> so the target environment is set up
    /// front.
    /// </remarks>
    /// <param name="description">Human-readable description of what this rule does.</param>
    public Rule(string description)
    {
        _description = description;
    }

    /// <summary>
    /// Initializes a new <see cref="Rule"/> with a description and target environment.
    /// </summary>
    /// <param name="description">Human-readable description of what this rule does.</param>
    /// <param name="environment">The environment key the rule targets. Set up front so
    /// the target environment is unambiguous when the built rule is passed to
    /// <c>Flag.AddRule</c>.</param>
    public Rule(string description, string environment)
    {
        _description = description;
        _environment = environment;
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
    /// Adds a condition using a raw operator string. Multiple conditions are combined with AND.
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
    /// Adds a condition using a typed <see cref="Op"/>. Multiple conditions are combined with AND.
    /// </summary>
    /// <param name="var">The variable path (e.g., "user.plan").</param>
    /// <param name="op">The typed comparison operator.</param>
    /// <param name="value">The value to compare against.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule When(string var, Op op, object? value) => When(var, OpWire[op], value);

    /// <summary>
    /// Adds a raw JSON Logic expression as a condition. Multiple conditions are combined with AND.
    /// </summary>
    /// <remarks>
    /// Escape hatch for predicates the <c>(var, op, value)</c> form can't express —
    /// OR, nested AND/OR, <c>if</c>, and so on. See <see href="https://jsonlogic.com/"/>
    /// for the full expression grammar.
    /// </remarks>
    /// <param name="jsonLogic">An arbitrary JSON Logic expression.</param>
    /// <returns>This rule for chaining.</returns>
    public Rule When(IDictionary<string, object?> jsonLogic)
    {
        _conditions.Add(new Dictionary<string, object?>(jsonLogic));
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
