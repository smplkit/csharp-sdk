using Smplkit.Errors;
using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

public class ConfigResolverTests
{
    // Helper: minimal valid options for testing
    private static SmplClientOptions MinimalOptions(
        string? apiKey = null,
        string? environment = null,
        string? service = null,
        string? baseDomain = null,
        string? scheme = null,
        string? profile = null,
        bool? debug = null,
        bool? disableTelemetry = null) =>
        new()
        {
            ApiKey = apiKey,
            Environment = environment,
            Service = service,
            BaseDomain = baseDomain,
            Scheme = scheme,
            Profile = profile,
            Debug = debug,
            DisableTelemetry = disableTelemetry,
        };

    // Helper: resolve with injectable overload, defaulting everything to null/empty
    private static ResolvedConfig Resolve(
        SmplClientOptions options,
        string? envApiKey = null,
        string? envBaseDomain = null,
        string? envScheme = null,
        string? envEnvironment = null,
        string? envService = null,
        string? envDebug = null,
        string? envDisableTelemetry = null,
        string? envProfile = null,
        string? fileContent = null)
    {
        var configPath = "/nonexistent/.smplkit";
        Func<string, string> reader = _ => fileContent ?? "";

        if (fileContent is not null)
        {
            // Create a temp file so File.Exists returns true
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(dir);
            configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, fileContent);
        }

        try
        {
            return ConfigResolver.Resolve(
                options, envApiKey, envBaseDomain, envScheme,
                envEnvironment, envService, envDebug, envDisableTelemetry,
                envProfile, configPath, reader);
        }
        finally
        {
            if (fileContent is not null)
            {
                var dir = Path.GetDirectoryName(configPath)!;
                try { Directory.Delete(dir, true); } catch { /* cleanup */ }
            }
        }
    }

    // ------------------------------------------------------------------
    // Step 1: Hardcoded defaults
    // ------------------------------------------------------------------

    [Fact]
    public void Defaults_BaseDomainIsSmplkitCom()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_test",
            envEnvironment: "prod",
            envService: "svc");
        Assert.Equal("smplkit.com", config.BaseDomain);
    }

    [Fact]
    public void Defaults_SchemeIsHttps()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_test",
            envEnvironment: "prod",
            envService: "svc");
        Assert.Equal("https", config.Scheme);
    }

    [Fact]
    public void Defaults_DebugIsFalse()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_test",
            envEnvironment: "prod",
            envService: "svc");
        Assert.False(config.Debug);
    }

    [Fact]
    public void Defaults_DisableTelemetryIsFalse()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_test",
            envEnvironment: "prod",
            envService: "svc");
        Assert.False(config.DisableTelemetry);
    }

    // ------------------------------------------------------------------
    // Step 2: Config file
    // ------------------------------------------------------------------

    [Fact]
    public void File_DefaultProfile_ReadsValues()
    {
        var ini = "[default]\napi_key = sk_file\nenvironment = staging\nservice = file-svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("sk_file", config.ApiKey);
        Assert.Equal("staging", config.Environment);
        Assert.Equal("file-svc", config.Service);
    }

    [Fact]
    public void File_CommonSection_MergedWithProfile()
    {
        var ini = "[common]\nbase_domain = custom.io\nscheme = http\n\n[default]\napi_key = sk_file\nenvironment = prod\nservice = svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("custom.io", config.BaseDomain);
        Assert.Equal("http", config.Scheme);
        Assert.Equal("sk_file", config.ApiKey);
    }

    [Fact]
    public void File_ProfileOverridesCommon()
    {
        var ini = "[common]\napi_key = sk_common\nenvironment = common-env\n\n[default]\napi_key = sk_profile\nenvironment = profile-env\nservice = svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("sk_profile", config.ApiKey);
        Assert.Equal("profile-env", config.Environment);
    }

    [Fact]
    public void File_NamedProfile_SelectedViaOptions()
    {
        var ini = "[default]\napi_key = sk_default\nenvironment = default-env\nservice = svc\n\n[staging]\napi_key = sk_staging\nenvironment = staging-env\nservice = staging-svc\n";
        var config = Resolve(MinimalOptions(profile: "staging"), fileContent: ini);
        Assert.Equal("sk_staging", config.ApiKey);
        Assert.Equal("staging-env", config.Environment);
    }

    [Fact]
    public void File_NamedProfile_SelectedViaEnvVar()
    {
        var ini = "[default]\napi_key = sk_default\nenvironment = default-env\nservice = svc\n\n[staging]\napi_key = sk_staging\nenvironment = staging-env\nservice = staging-svc\n";
        var config = Resolve(MinimalOptions(), envProfile: "staging", fileContent: ini);
        Assert.Equal("sk_staging", config.ApiKey);
        Assert.Equal("staging-env", config.Environment);
    }

    [Fact]
    public void File_ProfileOption_TakesPrecedenceOverEnvVar()
    {
        var ini = "[staging]\napi_key = sk_staging\nenvironment = staging\nservice = svc\n\n[production]\napi_key = sk_prod\nenvironment = production\nservice = svc\n";
        var config = Resolve(MinimalOptions(profile: "production"), envProfile: "staging", fileContent: ini);
        Assert.Equal("sk_prod", config.ApiKey);
        Assert.Equal("production", config.Environment);
    }

    [Fact]
    public void File_MissingNamedProfile_Throws()
    {
        var ini = "[production]\napi_key = sk_prod\nenvironment = prod\nservice = svc\n";
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(MinimalOptions(profile: "nonexistent"), fileContent: ini));
        Assert.Contains("[nonexistent]", ex.Message);
        Assert.Contains("production", ex.Message);
    }

    [Fact]
    public void File_MissingDefaultProfile_SilentlyProceeds()
    {
        // Default profile missing but other profiles exist -- should proceed silently
        var ini = "[staging]\napi_key = sk_staging\nenvironment = staging\nservice = svc\n";
        // No error, just falls through to env vars
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            envEnvironment: "env-env",
            envService: "env-svc",
            fileContent: ini);
        Assert.Equal("sk_env", config.ApiKey);
    }

    [Fact]
    public void File_BooleanValues_Debug()
    {
        var ini = "[default]\napi_key = sk_test\nenvironment = prod\nservice = svc\ndebug = yes\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.True(config.Debug);
    }

    [Fact]
    public void File_BooleanValues_DisableTelemetry()
    {
        var ini = "[default]\napi_key = sk_test\nenvironment = prod\nservice = svc\ndisable_telemetry = 1\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.True(config.DisableTelemetry);
    }

    [Fact]
    public void File_InvalidBoolean_Throws()
    {
        var ini = "[default]\napi_key = sk_test\nenvironment = prod\nservice = svc\ndebug = maybe\n";
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(MinimalOptions(), fileContent: ini));
        Assert.Contains("Invalid boolean", ex.Message);
        Assert.Contains("maybe", ex.Message);
    }

    [Fact]
    public void File_CommentsAndBlankLinesIgnored()
    {
        var ini = "# A comment\n\n[default]\n# another comment\napi_key = sk_test\n\nenvironment = prod\nservice = svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("sk_test", config.ApiKey);
    }

    [Fact]
    public void File_UnreadableFile_Skipped()
    {
        // Create a real file so File.Exists returns true, but the reader throws
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, ".smplkit");
        File.WriteAllText(configPath, "[default]\napi_key = sk_test\n");
        try
        {
            var config = ConfigResolver.Resolve(
                MinimalOptions(),
                envApiKey: "sk_env",
                envBaseDomain: null,
                envScheme: null,
                envEnvironment: "prod",
                envService: "svc",
                envDebug: null,
                envDisableTelemetry: null,
                envProfile: null,
                configPath: configPath,
                fileReader: _ => throw new IOException("simulated read error"));
            // Should not throw -- file read error is skipped
            Assert.Equal("sk_env", config.ApiKey);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void File_NonexistentFile_Skipped()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            envEnvironment: "prod",
            envService: "svc");
        Assert.Equal("sk_env", config.ApiKey);
    }

    [Fact]
    public void File_EmptyValues_TreatedAsUnset()
    {
        var ini = "[default]\napi_key = \nenvironment = prod\nservice = svc\n";
        // api_key is empty in file, should fall through
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            fileContent: ini);
        Assert.Equal("sk_env", config.ApiKey);
    }

    [Fact]
    public void File_LineWithoutEquals_Ignored()
    {
        var ini = "[default]\napi_key\nenvironment = prod\nservice = svc\n";
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            fileContent: ini);
        Assert.Equal("sk_env", config.ApiKey);
    }

    // ------------------------------------------------------------------
    // Step 3: Environment variables
    // ------------------------------------------------------------------

    [Fact]
    public void EnvVar_OverridesFileValues()
    {
        var ini = "[default]\napi_key = sk_file\nenvironment = file-env\nservice = file-svc\n";
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            envEnvironment: "env-env",
            envService: "env-svc",
            fileContent: ini);
        Assert.Equal("sk_env", config.ApiKey);
        Assert.Equal("env-env", config.Environment);
        Assert.Equal("env-svc", config.Service);
    }

    [Fact]
    public void EnvVar_BaseDomain_OverridesFile()
    {
        var ini = "[default]\napi_key = sk_test\nenvironment = prod\nservice = svc\nbase_domain = file.com\n";
        var config = Resolve(
            MinimalOptions(),
            envBaseDomain: "env.io",
            fileContent: ini);
        Assert.Equal("env.io", config.BaseDomain);
    }

    [Fact]
    public void EnvVar_Scheme_OverridesFile()
    {
        var ini = "[default]\napi_key = sk_test\nenvironment = prod\nservice = svc\nscheme = http\n";
        var config = Resolve(
            MinimalOptions(),
            envScheme: "https",
            fileContent: ini);
        Assert.Equal("https", config.Scheme);
    }

    [Fact]
    public void EnvVar_Debug_Boolean()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_test",
            envEnvironment: "prod",
            envService: "svc",
            envDebug: "true");
        Assert.True(config.Debug);
    }

    [Fact]
    public void EnvVar_DisableTelemetry_Boolean()
    {
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_test",
            envEnvironment: "prod",
            envService: "svc",
            envDisableTelemetry: "yes");
        Assert.True(config.DisableTelemetry);
    }

    [Fact]
    public void EnvVar_InvalidDebugBoolean_Throws()
    {
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(
                MinimalOptions(),
                envApiKey: "sk_test",
                envEnvironment: "prod",
                envService: "svc",
                envDebug: "invalid"));
        Assert.Contains("Invalid boolean", ex.Message);
    }

    [Fact]
    public void EnvVar_EmptyValues_TreatedAsUnset()
    {
        var ini = "[default]\napi_key = sk_file\nenvironment = prod\nservice = svc\n";
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "",
            fileContent: ini);
        Assert.Equal("sk_file", config.ApiKey);
    }

    // ------------------------------------------------------------------
    // Step 4: Constructor arguments
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_OverridesEnvVars()
    {
        var config = Resolve(
            MinimalOptions(
                apiKey: "sk_ctor",
                environment: "ctor-env",
                service: "ctor-svc"),
            envApiKey: "sk_env",
            envEnvironment: "env-env",
            envService: "env-svc");
        Assert.Equal("sk_ctor", config.ApiKey);
        Assert.Equal("ctor-env", config.Environment);
        Assert.Equal("ctor-svc", config.Service);
    }

    [Fact]
    public void Constructor_BaseDomain_OverridesEnv()
    {
        var config = Resolve(
            MinimalOptions(
                apiKey: "sk_test",
                environment: "prod",
                service: "svc",
                baseDomain: "ctor.io"),
            envBaseDomain: "env.io");
        Assert.Equal("ctor.io", config.BaseDomain);
    }

    [Fact]
    public void Constructor_Scheme_OverridesEnv()
    {
        var config = Resolve(
            MinimalOptions(
                apiKey: "sk_test",
                environment: "prod",
                service: "svc",
                scheme: "http"),
            envScheme: "https");
        Assert.Equal("http", config.Scheme);
    }

    [Fact]
    public void Constructor_Debug_OverridesEnv()
    {
        var config = Resolve(
            MinimalOptions(
                apiKey: "sk_test",
                environment: "prod",
                service: "svc",
                debug: false),
            envDebug: "true");
        Assert.False(config.Debug);
    }

    [Fact]
    public void Constructor_DisableTelemetry_OverridesEnv()
    {
        var config = Resolve(
            MinimalOptions(
                apiKey: "sk_test",
                environment: "prod",
                service: "svc",
                disableTelemetry: true),
            envDisableTelemetry: "false");
        Assert.True(config.DisableTelemetry);
    }

    // ------------------------------------------------------------------
    // Full 4-step precedence
    // ------------------------------------------------------------------

    [Fact]
    public void FullResolution_ConstructorWins()
    {
        var ini = "[common]\nbase_domain = common.io\n\n[default]\napi_key = sk_file\nenvironment = file-env\nservice = file-svc\nbase_domain = file.io\n";
        var config = Resolve(
            MinimalOptions(
                apiKey: "sk_ctor",
                environment: "ctor-env",
                service: "ctor-svc",
                baseDomain: "ctor.io"),
            envApiKey: "sk_env",
            envEnvironment: "env-env",
            envService: "env-svc",
            envBaseDomain: "env.io",
            fileContent: ini);
        Assert.Equal("sk_ctor", config.ApiKey);
        Assert.Equal("ctor-env", config.Environment);
        Assert.Equal("ctor-svc", config.Service);
        Assert.Equal("ctor.io", config.BaseDomain);
    }

    [Fact]
    public void FullResolution_EnvWinsOverFile()
    {
        var ini = "[default]\napi_key = sk_file\nenvironment = file-env\nservice = file-svc\n";
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            envEnvironment: "env-env",
            envService: "env-svc",
            fileContent: ini);
        Assert.Equal("sk_env", config.ApiKey);
        Assert.Equal("env-env", config.Environment);
        Assert.Equal("env-svc", config.Service);
    }

    [Fact]
    public void FullResolution_FileWinsOverDefaults()
    {
        var ini = "[default]\napi_key = sk_file\nenvironment = file-env\nservice = file-svc\nbase_domain = file.io\nscheme = http\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("file.io", config.BaseDomain);
        Assert.Equal("http", config.Scheme);
    }

    // ------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------

    [Fact]
    public void Validation_MissingEnvironment_Throws()
    {
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(
                MinimalOptions(apiKey: "sk_test", service: "svc")));
        Assert.Contains("No environment provided", ex.Message);
        Assert.Contains("SMPLKIT_ENVIRONMENT", ex.Message);
        Assert.Contains("~/.smplkit", ex.Message);
    }

    [Fact]
    public void Validation_MissingService_Throws()
    {
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(
                MinimalOptions(apiKey: "sk_test", environment: "prod")));
        Assert.Contains("No service provided", ex.Message);
        Assert.Contains("SMPLKIT_SERVICE", ex.Message);
        Assert.Contains("~/.smplkit", ex.Message);
    }

    [Fact]
    public void Validation_MissingApiKey_Throws()
    {
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(
                MinimalOptions(environment: "prod", service: "svc")));
        Assert.Contains("No API key provided", ex.Message);
        Assert.Contains("SMPLKIT_API_KEY", ex.Message);
        Assert.Contains("~/.smplkit", ex.Message);
    }

    [Fact]
    public void Validation_ErrorShowsActiveProfile()
    {
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(
                MinimalOptions(profile: "staging", environment: "prod", service: "svc")));
        Assert.Contains("[staging]", ex.Message);
    }

    [Fact]
    public void Validation_EnvironmentCheckedFirst()
    {
        // When both environment and service are missing, error is about environment
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(MinimalOptions(apiKey: "sk_test")));
        Assert.Contains("environment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validation_ServiceCheckedBeforeApiKey()
    {
        // When both service and api_key are missing, error is about service
        var ex = Assert.Throws<SmplException>(() =>
            Resolve(MinimalOptions(environment: "prod")));
        Assert.Contains("service", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // ServiceUrl helper
    // ------------------------------------------------------------------

    [Fact]
    public void ServiceUrl_Https()
    {
        Assert.Equal("https://config.smplkit.com", ConfigResolver.ServiceUrl("https", "config", "smplkit.com"));
    }

    [Fact]
    public void ServiceUrl_Http()
    {
        Assert.Equal("http://flags.localhost", ConfigResolver.ServiceUrl("http", "flags", "localhost"));
    }

    // ------------------------------------------------------------------
    // ParseBool
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    public void ParseBool_ValidValues(string input, bool expected)
    {
        Assert.Equal(expected, ConfigResolver.ParseBool(input, "test_key"));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("nope")]
    public void ParseBool_InvalidValues_Throws(string input)
    {
        var ex = Assert.Throws<SmplException>(() => ConfigResolver.ParseBool(input, "test_key"));
        Assert.Contains("Invalid boolean", ex.Message);
    }

    [Fact]
    public void ParseBool_WhitespaceIsTrimmed()
    {
        Assert.True(ConfigResolver.ParseBool("  true  ", "test_key"));
    }

    // ------------------------------------------------------------------
    // INI edge cases
    // ------------------------------------------------------------------

    [Fact]
    public void Ini_MalformedContent_NoSections()
    {
        // No sections at all -- no file values, falls through to env vars
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            envEnvironment: "prod",
            envService: "svc",
            fileContent: "not valid ini\nno sections");
        Assert.Equal("sk_env", config.ApiKey);
    }

    [Fact]
    public void Ini_KeysOutsideSections_Ignored()
    {
        var ini = "api_key = sk_orphan\n\n[default]\nenvironment = prod\nservice = svc\n";
        // api_key outside any section should be ignored
        var config = Resolve(
            MinimalOptions(),
            envApiKey: "sk_env",
            fileContent: ini);
        Assert.Equal("sk_env", config.ApiKey);
    }

    [Fact]
    public void Ini_SectionHeaderWithSpaces()
    {
        var ini = "[ default ]\napi_key = sk_test\nenvironment = prod\nservice = svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        // Section name is trimmed
        Assert.Equal("sk_test", config.ApiKey);
    }

    [Fact]
    public void Ini_CaseInsensitiveSections()
    {
        var ini = "[DEFAULT]\napi_key = sk_test\nenvironment = prod\nservice = svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("sk_test", config.ApiKey);
    }

    [Fact]
    public void Ini_CaseInsensitiveKeys()
    {
        var ini = "[default]\nAPI_KEY = sk_test\nENVIRONMENT = prod\nSERVICE = svc\n";
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("sk_test", config.ApiKey);
    }

    // ------------------------------------------------------------------
    // All config keys from file
    // ------------------------------------------------------------------

    [Fact]
    public void File_AllKeys_Resolved()
    {
        var ini = string.Join("\n",
            "[default]",
            "api_key = sk_all",
            "base_domain = all.io",
            "scheme = http",
            "environment = all-env",
            "service = all-svc",
            "debug = true",
            "disable_telemetry = yes",
            "");
        var config = Resolve(MinimalOptions(), fileContent: ini);
        Assert.Equal("sk_all", config.ApiKey);
        Assert.Equal("all.io", config.BaseDomain);
        Assert.Equal("http", config.Scheme);
        Assert.Equal("all-env", config.Environment);
        Assert.Equal("all-svc", config.Service);
        Assert.True(config.Debug);
        Assert.True(config.DisableTelemetry);
    }
}
