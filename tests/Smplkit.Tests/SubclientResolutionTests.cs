using System.Reflection;
using Smplkit.Audit;
using Smplkit.Jobs;
using Xunit;
using ConfigClient = Smplkit.Config.ConfigClient;
using FlagsClient = Smplkit.Flags.FlagsClient;
using LoggingClient = Smplkit.Logging.LoggingClient;

namespace Smplkit.Tests;

/// <summary>
/// Uniform environment/service resolution across every standalone sub-client
/// that has an environment concept (Config, Flags, Logging, Audit, Jobs): the
/// value resolves through the same chain as <see cref="Smplkit.SmplClient"/> —
/// defaults → <c>~/.smplkit</c> → <c>SMPLKIT_ENVIRONMENT</c> /
/// <c>SMPLKIT_SERVICE</c> → constructor argument — and an explicit constructor
/// argument always wins.
/// </summary>
public class SubclientResolutionTests
{
    private const string Key = "sk_resolution_test";
    private const string Domain = "example.test";
    private const string EnvVarEnvironment = "envvar-environment";
    private const string EnvVarService = "envvar-service";

    private static string? Field(object target, string name)
        => (string?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);

    /// <summary>Runs <paramref name="body"/> with SMPLKIT_ENVIRONMENT / SMPLKIT_SERVICE pinned.</summary>
    private static void WithEnvVars(Action body)
    {
        var savedEnv = Environment.GetEnvironmentVariable("SMPLKIT_ENVIRONMENT");
        var savedSvc = Environment.GetEnvironmentVariable("SMPLKIT_SERVICE");
        try
        {
            Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", EnvVarEnvironment);
            Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", EnvVarService);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMPLKIT_ENVIRONMENT", savedEnv);
            Environment.SetEnvironmentVariable("SMPLKIT_SERVICE", savedSvc);
        }
    }

    // ------------------------------------------------------------------
    // Environment / service resolve from SMPLKIT_* when omitted
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigClient_ResolvesEnvironmentAndService_FromEnvVars()
    {
        WithEnvVars(() =>
        {
            using var config = new ConfigClient(apiKey: Key, baseDomain: Domain, telemetry: false);
            Assert.Equal(EnvVarEnvironment, Field(config, "_environment"));
            Assert.Equal(EnvVarService, Field(config, "_service"));
        });
    }

    [Fact]
    public void FlagsClient_ResolvesEnvironmentAndService_FromEnvVars()
    {
        WithEnvVars(() =>
        {
            using var flags = new FlagsClient(apiKey: Key, baseDomain: Domain, telemetry: false);
            Assert.Equal(EnvVarEnvironment, Field(flags, "_environment"));
            Assert.Equal(EnvVarService, Field(flags, "_service"));
        });
    }

    [Fact]
    public void LoggingClient_ResolvesEnvironmentAndService_FromEnvVars()
    {
        WithEnvVars(() =>
        {
            using var logging = new LoggingClient(apiKey: Key, baseDomain: Domain, telemetry: false);
            Assert.Equal(EnvVarEnvironment, Field(logging, "_environment"));
            Assert.Equal(EnvVarService, Field(logging, "_service"));
        });
    }

    [Fact]
    public void AuditClient_ResolvesEnvironment_FromEnvVars()
    {
        WithEnvVars(() =>
        {
            var audit = new AuditClient(apiKey: Key, baseDomain: Domain);
            Assert.Equal(EnvVarEnvironment, Field(audit.Events, "_environment"));
            audit.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void JobsClient_ResolvesEnvironment_FromEnvVars()
    {
        WithEnvVars(() =>
        {
            using var jobs = new JobsClient(apiKey: Key, baseDomain: Domain);
            Assert.Equal(EnvVarEnvironment, Field(jobs, "_environment"));
        });
    }

    // ------------------------------------------------------------------
    // An explicit constructor argument always wins over the environment
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigClient_ExplicitArguments_WinOverEnvVars()
    {
        WithEnvVars(() =>
        {
            using var config = new ConfigClient(
                apiKey: Key, environment: "explicit-env", service: "explicit-svc",
                baseDomain: Domain, telemetry: false);
            Assert.Equal("explicit-env", Field(config, "_environment"));
            Assert.Equal("explicit-svc", Field(config, "_service"));
        });
    }

    [Fact]
    public void FlagsClient_ExplicitArguments_WinOverEnvVars()
    {
        WithEnvVars(() =>
        {
            using var flags = new FlagsClient(
                apiKey: Key, environment: "explicit-env", service: "explicit-svc",
                baseDomain: Domain, telemetry: false);
            Assert.Equal("explicit-env", Field(flags, "_environment"));
            Assert.Equal("explicit-svc", Field(flags, "_service"));
        });
    }

    [Fact]
    public void LoggingClient_ExplicitArguments_WinOverEnvVars()
    {
        WithEnvVars(() =>
        {
            using var logging = new LoggingClient(
                apiKey: Key, environment: "explicit-env", service: "explicit-svc",
                baseDomain: Domain, telemetry: false);
            Assert.Equal("explicit-env", Field(logging, "_environment"));
            Assert.Equal("explicit-svc", Field(logging, "_service"));
        });
    }

    [Fact]
    public void AuditClient_ExplicitEnvironment_WinsOverEnvVars()
    {
        WithEnvVars(() =>
        {
            var audit = new AuditClient(apiKey: Key, environment: "explicit-env", baseDomain: Domain);
            Assert.Equal("explicit-env", Field(audit.Events, "_environment"));
            audit.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void JobsClient_ExplicitEnvironment_WinsOverEnvVars()
    {
        WithEnvVars(() =>
        {
            using var jobs = new JobsClient(apiKey: Key, baseDomain: Domain, environment: "explicit-env");
            Assert.Equal("explicit-env", Field(jobs, "_environment"));
        });
    }

    // ------------------------------------------------------------------
    // Parent-wired values always win over resolution
    // ------------------------------------------------------------------

    [Fact]
    public void AuditClient_ParentWiredPath_UsesEnvironmentVerbatim()
    {
        WithEnvVars(() =>
        {
            // apiKey + baseUrl both supplied is the top-level-client path: the
            // caller has already resolved everything and no re-resolution runs.
            var audit = new AuditClient(
                apiKey: Key, environment: "parent-env", baseUrl: "https://audit.example.test");
            Assert.Equal("parent-env", Field(audit.Events, "_environment"));
            audit.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }
}
