using Smplkit.Errors;

namespace Smplkit.Internal;

/// <summary>
/// Fully resolved SDK configuration after the 4-step resolution.
/// </summary>
internal sealed record ResolvedConfig(
    string ApiKey,
    string BaseDomain,
    string Scheme,
    string Environment,
    string Service,
    bool Debug,
    bool DisableTelemetry);

/// <summary>
/// Resolves SDK configuration using a 4-step algorithm:
/// <list type="number">
///   <item>SDK hardcoded defaults</item>
///   <item>Configuration file (<c>~/.smplkit</c>): [common] + selected profile</item>
///   <item>Environment variables (<c>SMPLKIT_*</c>)</item>
///   <item>Constructor arguments (<see cref="SmplClientOptions"/>)</item>
/// </list>
/// </summary>
internal static class ConfigResolver
{
    /// <summary>
    /// Resolves configuration using real environment and filesystem.
    /// </summary>
    internal static ResolvedConfig Resolve(SmplClientOptions options)
    {
        return Resolve(
            options,
            envApiKey: System.Environment.GetEnvironmentVariable("SMPLKIT_API_KEY"),
            envBaseDomain: System.Environment.GetEnvironmentVariable("SMPLKIT_BASE_DOMAIN"),
            envScheme: System.Environment.GetEnvironmentVariable("SMPLKIT_SCHEME"),
            envEnvironment: System.Environment.GetEnvironmentVariable("SMPLKIT_ENVIRONMENT"),
            envService: System.Environment.GetEnvironmentVariable("SMPLKIT_SERVICE"),
            envDebug: System.Environment.GetEnvironmentVariable("SMPLKIT_DEBUG"),
            envDisableTelemetry: System.Environment.GetEnvironmentVariable("SMPLKIT_DISABLE_TELEMETRY"),
            envProfile: System.Environment.GetEnvironmentVariable("SMPLKIT_PROFILE"),
            configPath: Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".smplkit"),
            fileReader: File.ReadAllText);
    }

    /// <summary>
    /// Injectable overload for testing. All external dependencies are parameters.
    /// </summary>
    internal static ResolvedConfig Resolve(
        SmplClientOptions options,
        string? envApiKey,
        string? envBaseDomain,
        string? envScheme,
        string? envEnvironment,
        string? envService,
        string? envDebug,
        string? envDisableTelemetry,
        string? envProfile,
        string configPath,
        Func<string, string> fileReader)
    {
        // Step 1: Hardcoded defaults
        string? apiKey = null;
        string baseDomain = "smplkit.com";
        string scheme = "https";
        string? environment = null;
        string? service = null;
        bool debug = false;
        bool disableTelemetry = false;

        // Determine profile: constructor arg > SMPLKIT_PROFILE env > "default"
        var activeProfile = options.Profile
            ?? (string.IsNullOrEmpty(envProfile) ? null : envProfile)
            ?? "default";

        // Step 2: Configuration file
        var fileValues = ReadConfigFile(configPath, activeProfile, fileReader);
        ApplyFileValues(fileValues, activeProfile,
            ref apiKey, ref baseDomain, ref scheme, ref environment,
            ref service, ref debug, ref disableTelemetry);

        // Step 3: Environment variables
        if (!string.IsNullOrEmpty(envApiKey))
            apiKey = envApiKey;
        if (!string.IsNullOrEmpty(envBaseDomain))
            baseDomain = envBaseDomain;
        if (!string.IsNullOrEmpty(envScheme))
            scheme = envScheme;
        if (!string.IsNullOrEmpty(envEnvironment))
            environment = envEnvironment;
        if (!string.IsNullOrEmpty(envService))
            service = envService;
        ApplyEnvBool(envDebug, "SMPLKIT_DEBUG", ref debug);
        ApplyEnvBool(envDisableTelemetry, "SMPLKIT_DISABLE_TELEMETRY", ref disableTelemetry);

        // Step 4: Constructor arguments
        if (!string.IsNullOrEmpty(options.ApiKey))
            apiKey = options.ApiKey;
        if (options.BaseDomain is not null)
            baseDomain = options.BaseDomain;
        if (options.Scheme is not null)
            scheme = options.Scheme;
        if (!string.IsNullOrEmpty(options.Environment))
            environment = options.Environment;
        if (!string.IsNullOrEmpty(options.Service))
            service = options.Service;
        if (options.Debug is not null)
            debug = options.Debug.Value;
        if (options.DisableTelemetry is not null)
            disableTelemetry = options.DisableTelemetry.Value;

        // Validate required fields
        if (string.IsNullOrEmpty(environment))
            throw new SmplException(
                "No environment provided. Set one of:\n" +
                "  1. Pass Environment in SmplClientOptions\n" +
                "  2. Set the SMPLKIT_ENVIRONMENT environment variable\n" +
                $"  3. Add environment to the [{activeProfile}] section in ~/.smplkit");

        if (string.IsNullOrEmpty(service))
            throw new SmplException(
                "No service provided. Set one of:\n" +
                "  1. Pass Service in SmplClientOptions\n" +
                "  2. Set the SMPLKIT_SERVICE environment variable\n" +
                $"  3. Add service to the [{activeProfile}] section in ~/.smplkit");

        if (string.IsNullOrEmpty(apiKey))
            throw new SmplException(
                "No API key provided. Set one of:\n" +
                "  1. Pass ApiKey in SmplClientOptions\n" +
                "  2. Set the SMPLKIT_API_KEY environment variable\n" +
                $"  3. Add api_key to the [{activeProfile}] section in ~/.smplkit");

        return new ResolvedConfig(
            ApiKey: apiKey!,
            BaseDomain: baseDomain,
            Scheme: scheme,
            Environment: environment!,
            Service: service!,
            Debug: debug,
            DisableTelemetry: disableTelemetry);
    }

    /// <summary>
    /// Build a service URL: {scheme}://{subdomain}.{baseDomain}.
    /// </summary>
    internal static string ServiceUrl(string scheme, string subdomain, string baseDomain)
        => $"{scheme}://{subdomain}.{baseDomain}";

    // ------------------------------------------------------------------
    // INI file parser
    // ------------------------------------------------------------------

    /// <summary>
    /// Read ~/.smplkit and return merged [common] + profile values.
    /// Returns an empty dictionary if the file doesn't exist or is unreadable.
    /// </summary>
    private static Dictionary<string, string> ReadConfigFile(
        string configPath,
        string profile,
        Func<string, string> fileReader)
    {
        if (!File.Exists(configPath))
            return new Dictionary<string, string>();

        string content;
        try
        {
            content = fileReader(configPath);
        }
        catch
        {
            return new Dictionary<string, string>();
        }

        var sections = ParseIni(content);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Load [common] first
        if (sections.TryGetValue("common", out var commonValues))
        {
            foreach (var (key, val) in commonValues)
            {
                if (!string.IsNullOrEmpty(val))
                    values[key] = val;
            }
        }

        // Overlay the selected profile section
        if (sections.TryGetValue(profile, out var profileValues))
        {
            foreach (var (key, val) in profileValues)
            {
                if (!string.IsNullOrEmpty(val))
                    values[key] = val;
            }
        }
        else
        {
            // If profile not found but other non-common sections exist, error for non-default
            var nonCommonSections = sections.Keys
                .Where(s => !s.Equals("common", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (nonCommonSections.Count > 0 && !profile.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                throw new SmplException(
                    $"Profile [{profile}] not found in ~/.smplkit. " +
                    $"Available profiles: {string.Join(", ", nonCommonSections)}");
            }
        }

        return values;
    }

    /// <summary>
    /// Parse INI content into a dictionary of section -> (key -> value).
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> ParseIni(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? currentSection = null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var sectionName = trimmed[1..^1].Trim();
                if (!sections.TryGetValue(sectionName, out currentSection))
                {
                    currentSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[sectionName] = currentSection;
                }
                continue;
            }

            if (currentSection is not null)
            {
                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex != -1)
                {
                    var key = trimmed[..eqIndex].Trim();
                    var value = trimmed[(eqIndex + 1)..].Trim();
                    if (key.Length > 0 && value.Length > 0)
                        currentSection[key] = value;
                }
            }
        }

        return sections;
    }

    // ------------------------------------------------------------------
    // Apply helpers
    // ------------------------------------------------------------------

    private static void ApplyFileValues(
        Dictionary<string, string> fileValues,
        string activeProfile,
        ref string? apiKey,
        ref string baseDomain,
        ref string scheme,
        ref string? environment,
        ref string? service,
        ref bool debug,
        ref bool disableTelemetry)
    {
        if (fileValues.TryGetValue("api_key", out var fileApiKey))
            apiKey = fileApiKey;
        if (fileValues.TryGetValue("base_domain", out var fileBd))
            baseDomain = fileBd;
        if (fileValues.TryGetValue("scheme", out var fileScheme))
            scheme = fileScheme;
        if (fileValues.TryGetValue("environment", out var fileEnv))
            environment = fileEnv;
        if (fileValues.TryGetValue("service", out var fileSvc))
            service = fileSvc;
        if (fileValues.TryGetValue("debug", out var fileDebug))
            debug = ParseBool(fileDebug, "debug");
        if (fileValues.TryGetValue("disable_telemetry", out var fileDt))
            disableTelemetry = ParseBool(fileDt, "disable_telemetry");
    }

    private static void ApplyEnvBool(string? envVal, string envVarName, ref bool target)
    {
        if (!string.IsNullOrEmpty(envVal))
            target = ParseBool(envVal, envVarName);
    }

    /// <summary>
    /// Parse a boolean string: true/1/yes or false/0/no (case-insensitive).
    /// </summary>
    internal static bool ParseBool(string value, string key)
    {
        var lower = value.Trim().ToLowerInvariant();
        return lower switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new SmplException(
                $"Invalid boolean value for {key}: \"{value}\". " +
                "Expected one of: true, false, 1, 0, yes, no"),
        };
    }
}
