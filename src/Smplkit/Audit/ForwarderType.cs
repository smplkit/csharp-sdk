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
    /// <summary>Datadog Logs Intake.</summary>
    Datadog,
    /// <summary>Elastic Logs ingest API.</summary>
    Elastic,
    /// <summary>Honeycomb Events API.</summary>
    Honeycomb,
    /// <summary>Generic HTTP/HTTPS endpoint.</summary>
    Http,
    /// <summary>New Relic Log API.</summary>
    NewRelic,
    /// <summary>Splunk HTTP Event Collector.</summary>
    SplunkHec,
    /// <summary>Sumo Logic HTTP Source.</summary>
    SumoLogic,
}

/// <summary>Wire-value conversions for <see cref="ForwarderType"/>.</summary>
public static class ForwarderTypeExtensions
{
    private static readonly IReadOnlyDictionary<ForwarderType, string> _toWire =
        new Dictionary<ForwarderType, string>
        {
            { ForwarderType.Datadog, "DATADOG" },
            { ForwarderType.Elastic, "ELASTIC" },
            { ForwarderType.Honeycomb, "HONEYCOMB" },
            { ForwarderType.Http, "HTTP" },
            { ForwarderType.NewRelic, "NEW_RELIC" },
            { ForwarderType.SplunkHec, "SPLUNK_HEC" },
            { ForwarderType.SumoLogic, "SUMO_LOGIC" },
        };

    private static readonly IReadOnlyDictionary<string, ForwarderType> _fromWire =
        new Dictionary<string, ForwarderType>(StringComparer.Ordinal)
        {
            { "DATADOG", ForwarderType.Datadog },
            { "ELASTIC", ForwarderType.Elastic },
            { "HONEYCOMB", ForwarderType.Honeycomb },
            { "HTTP", ForwarderType.Http },
            { "NEW_RELIC", ForwarderType.NewRelic },
            { "SPLUNK_HEC", ForwarderType.SplunkHec },
            { "SUMO_LOGIC", ForwarderType.SumoLogic },
        };

    /// <summary>Returns the wire-format slug — e.g. <c>"SPLUNK_HEC"</c>.</summary>
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
