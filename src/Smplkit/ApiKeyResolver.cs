using Smplkit.Errors;

namespace Smplkit;

/// <summary>
/// Resolves an API key from an explicit value, environment variable, or config file.
/// </summary>
internal static class ApiKeyResolver
{
    private const string NoApiKeyMessage =
        "No API key provided. Set one of:\n" +
        "  1. Set ApiKey in SmplClientOptions\n" +
        "  2. Set the SMPLKIT_API_KEY environment variable\n" +
        "  3. Create a ~/.smplkit file with:\n" +
        "     [default]\n" +
        "     api_key = your_key_here";

    internal static string Resolve(string? explicitKey)
    {
        return Resolve(
            explicitKey,
            Environment.GetEnvironmentVariable("SMPLKIT_API_KEY"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".smplkit"));
    }

    internal static string Resolve(string? explicitKey, string? envVal, string configPath)
    {
        if (!string.IsNullOrEmpty(explicitKey))
            return explicitKey;

        if (!string.IsNullOrEmpty(envVal))
            return envVal;

        if (File.Exists(configPath))
        {
            try
            {
                var apiKey = ParseIniApiKey(File.ReadAllText(configPath));
                if (apiKey != null)
                    return apiKey;
            }
            catch
            {
                // Unreadable file — skip
            }
        }

        throw new SmplException(NoApiKeyMessage);
    }

    private static string? ParseIniApiKey(string content)
    {
        var inDefault = false;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (trimmed.StartsWith('['))
            {
                inDefault = trimmed.Equals("[default]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inDefault && trimmed.StartsWith("api_key"))
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
