using Smplkit.Errors;

namespace Smplkit;

/// <summary>
/// Resolves an API key from an explicit value, environment variable, or config file.
/// </summary>
internal static class ApiKeyResolver
{
    internal static string NoApiKeyMessage(string environment) =>
        "No API key provided. Set one of:\n" +
        "  1. Set ApiKey in SmplClientOptions\n" +
        "  2. Set the SMPLKIT_API_KEY environment variable\n" +
        "  3. Create a ~/.smplkit file with:\n" +
        $"     [{environment}]\n" +
        "     api_key = your_key_here";

    internal static string Resolve(string? explicitKey, string environment)
    {
        return Resolve(
            explicitKey,
            Environment.GetEnvironmentVariable("SMPLKIT_API_KEY"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".smplkit"),
            environment);
    }

    internal static string Resolve(string? explicitKey, string? envVal, string configPath, string environment)
    {
        return Resolve(explicitKey, envVal, configPath, environment, File.ReadAllText);
    }

    internal static string Resolve(string? explicitKey, string? envVal, string configPath, string environment, Func<string, string> fileReader)
    {
        if (!string.IsNullOrEmpty(explicitKey))
            return explicitKey;

        if (!string.IsNullOrEmpty(envVal))
            return envVal;

        if (File.Exists(configPath))
        {
            try
            {
                var apiKey = ParseIniApiKey(fileReader(configPath), environment);
                if (apiKey != null)
                    return apiKey;
            }
            catch
            {
                // Unreadable file — skip
            }
        }

        throw new SmplException(NoApiKeyMessage(environment));
    }

    private static string? ParseIniApiKey(string content, string environment)
    {
        // Try [environment] section first, then [default]
        var envKey = TryParseSection(content, environment);
        if (envKey != null) return envKey;
        return TryParseSection(content, "default");
    }

    private static string? TryParseSection(string content, string section)
    {
        var inSection = false;
        var sectionHeader = $"[{section}]";
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (trimmed.StartsWith('['))
            {
                inSection = trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection && trimmed.StartsWith("api_key"))
            {
                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex != -1)
                {
                    var value = trimmed[(eqIndex + 1)..].Trim();
                    if (value.Length > 0)
                        return value;
                }
            }
        }
        return null;
    }
}
