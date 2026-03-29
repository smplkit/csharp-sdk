using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests;

public class ApiKeyResolverTests : IDisposable
{
    private readonly string? _originalEnv;
    private readonly string? _originalHome;

    public ApiKeyResolverTests()
    {
        _originalEnv = Environment.GetEnvironmentVariable("SMPLKIT_API_KEY");
        _originalHome = Environment.GetEnvironmentVariable("HOME");
    }

    public void Dispose()
    {
        if (_originalEnv is not null)
            Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", _originalEnv);
        else
            Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);

        if (_originalHome is not null)
            Environment.SetEnvironmentVariable("HOME", _originalHome);
        else
            Environment.SetEnvironmentVariable("HOME", null);
    }

    [Fact]
    public void ExplicitKey_IsReturned()
    {
        Assert.Equal("sk_api_explicit", ApiKeyResolver.Resolve("sk_api_explicit"));
    }

    [Fact]
    public void EnvVar_UsedWhenNoExplicit()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_api_env");
        Assert.Equal("sk_api_env", ApiKeyResolver.Resolve(null));
    }

    [Fact]
    public void ConfigFile_UsedWhenNoExplicitNoEnv()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".smplkit"), "[default]\napi_key = \"sk_api_file\"\n");
            Environment.SetEnvironmentVariable("HOME", dir);
            Assert.Equal("sk_api_file", ApiKeyResolver.Resolve(null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Throws_WhenNoKeyAnywhere()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            Environment.SetEnvironmentVariable("HOME", dir);
            var ex = Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null));
            Assert.Contains("No API key provided", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ErrorMessage_ListsAllMethods()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            Environment.SetEnvironmentVariable("HOME", dir);
            var ex = Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null));
            Assert.Contains("SmplClientOptions", ex.Message);
            Assert.Contains("SMPLKIT_API_KEY", ex.Message);
            Assert.Contains("~/.smplkit", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExplicitKey_TakesPrecedenceOverEnv()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_api_env");
        Assert.Equal("sk_api_explicit", ApiKeyResolver.Resolve("sk_api_explicit"));
    }

    [Fact]
    public void EnvVar_TakesPrecedenceOverFile()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "sk_api_env");
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".smplkit"), "[default]\napi_key = \"sk_api_file\"\n");
            Environment.SetEnvironmentVariable("HOME", dir);
            Assert.Equal("sk_api_env", ApiKeyResolver.Resolve(null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmptyEnvVar_TreatedAsUnset()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", "");
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".smplkit"), "[default]\napi_key = \"sk_api_file\"\n");
            Environment.SetEnvironmentVariable("HOME", dir);
            Assert.Equal("sk_api_file", ApiKeyResolver.Resolve(null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MalformedFile_IsSkipped()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".smplkit"), "not valid toml");
            Environment.SetEnvironmentVariable("HOME", dir);
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FileWithoutApiKey_Throws()
    {
        Environment.SetEnvironmentVariable("SMPLKIT_API_KEY", null);
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".smplkit"), "[default]\nother_key = \"value\"\n");
            Environment.SetEnvironmentVariable("HOME", dir);
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
