// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Smplkit.Audit;

/// <summary>
/// SIEM streaming destination type.
/// </summary>
/// <remarks>
/// <para>Mirrors the audit OpenAPI <c>ForwarderType</c> enum so the
/// wrapper public surface keeps customer code outside the
/// <c>Smplkit.Internal.*</c> namespace. ADR-047 §2.12.</para>
/// </remarks>
public enum ForwarderType
{
    /// <summary>Generic HTTP/HTTPS endpoint.</summary>
    Http,
    /// <summary>Datadog Logs Intake.</summary>
    Datadog,
    /// <summary>Splunk HTTP Event Collector.</summary>
    SplunkHec,
    /// <summary>Sumo Logic HTTP Source.</summary>
    SumoLogic,
    /// <summary>New Relic Log API.</summary>
    NewRelic,
    /// <summary>Honeycomb Events API.</summary>
    Honeycomb,
    /// <summary>Elastic Logs ingest API.</summary>
    Elastic,
}

/// <summary>Wire-value conversions for <see cref="ForwarderType"/>.</summary>
public static class ForwarderTypeExtensions
{
    private static readonly IReadOnlyDictionary<ForwarderType, string> _toWire =
        new Dictionary<ForwarderType, string>
        {
            { ForwarderType.Http, "http" },
            { ForwarderType.Datadog, "datadog" },
            { ForwarderType.SplunkHec, "splunk_hec" },
            { ForwarderType.SumoLogic, "sumo_logic" },
            { ForwarderType.NewRelic, "new_relic" },
            { ForwarderType.Honeycomb, "honeycomb" },
            { ForwarderType.Elastic, "elastic" },
        };

    private static readonly IReadOnlyDictionary<string, ForwarderType> _fromWire =
        new Dictionary<string, ForwarderType>(StringComparer.Ordinal)
        {
            { "http", ForwarderType.Http },
            { "datadog", ForwarderType.Datadog },
            { "splunk_hec", ForwarderType.SplunkHec },
            { "sumo_logic", ForwarderType.SumoLogic },
            { "new_relic", ForwarderType.NewRelic },
            { "honeycomb", ForwarderType.Honeycomb },
            { "elastic", ForwarderType.Elastic },
        };

    /// <summary>Returns the wire-format slug — e.g. <c>"splunk_hec"</c>.</summary>
    public static string ToWireValue(this ForwarderType value) => _toWire[value];

    /// <summary>Parse a wire-format slug. Throws on unknown values.</summary>
    public static ForwarderType FromWireValue(string value)
    {
        if (_fromWire.TryGetValue(value, out var t))
        {
            return t;
        }
        throw new ArgumentException($"Unknown ForwarderType: {value}", nameof(value));
    }
}
