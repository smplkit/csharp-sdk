using System.Text.RegularExpressions;
using Smplkit.Errors;

namespace Smplkit;

/// <summary>
/// Resolves an API key from an explicit value, environment variable, or config file.
/// </summary>
internal static partial class ApiKeyResolver
{
    private const string NoApiKeyMessage =
        "No API key provided. Set one of:\n" +
        "  1. Set ApiKey in SmplClientOptions\n" +
        "  2. Set the SMPLKIT_API_KEY environment variable\n" +
        "  3. Add api_key to [default] in ~/.smplkit";

    internal static string Resolve(string? explicitKey)
    {
        if (!string.IsNullOrEmpty(explicitKey))
            return explicitKey;

        var envVal = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY");
        if (!string.IsNullOrEmpty(envVal))
            return envVal;

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".smplkit");

        if (File.Exists(configPath))
        {
            try
            {
                var content = File.ReadAllText(configPath);
                var match = ApiKeyPattern().Match(content);
                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch
            {
                // Malformed file — skip
            }
        }

        throw new SmplException(NoApiKeyMessage);
    }

    [GeneratedRegex(@"\[default\]\s*[\s\S]*?api_key\s*=\s*""([^""]+)""")]
    private static partial Regex ApiKeyPattern();
}
