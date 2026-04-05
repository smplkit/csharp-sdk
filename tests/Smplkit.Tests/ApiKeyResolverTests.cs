using Smplkit.Errors;
using Xunit;

namespace Smplkit.Tests;

[Collection("EnvironmentTests")]
public class ApiKeyResolverTests
{
    [Fact]
    public void ExplicitKey_IsReturned()
    {
        Assert.Equal("sk_api_explicit", ApiKeyResolver.Resolve("sk_api_explicit", null, "/nonexistent", "test"));
    }

    [Fact]
    public void EnvVar_UsedWhenNoExplicit()
    {
        Assert.Equal("sk_api_env", ApiKeyResolver.Resolve(null, "sk_api_env", "/nonexistent", "test"));
    }

    [Fact]
    public void ConfigFile_UsedWhenNoExplicitNoEnv()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\napi_key = sk_api_file\n");
            Assert.Equal("sk_api_file", ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Throws_WhenNoKeyAnywhere()
    {
        var configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), ".smplkit");
        var ex = Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "production"));
        Assert.Contains("No API key provided", ex.Message);
    }

    [Fact]
    public void ErrorMessage_ListsAllMethods()
    {
        var configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), ".smplkit");
        var ex = Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "production"));
        Assert.Contains("SmplClientOptions", ex.Message);
        Assert.Contains("SMPLKIT_API_KEY", ex.Message);
        Assert.Contains("~/.smplkit", ex.Message);
    }

    [Fact]
    public void ErrorMessage_ShowsResolvedEnvironment()
    {
        var configPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), ".smplkit");
        var ex = Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "staging"));
        Assert.Contains("[staging]", ex.Message);
    }

    [Fact]
    public void ExplicitKey_TakesPrecedenceOverEnv()
    {
        Assert.Equal("sk_api_explicit", ApiKeyResolver.Resolve("sk_api_explicit", "sk_api_env", "/nonexistent", "test"));
    }

    [Fact]
    public void EnvVar_TakesPrecedenceOverFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\napi_key = sk_api_file\n");
            Assert.Equal("sk_api_env", ApiKeyResolver.Resolve(null, "sk_api_env", configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmptyEnvVar_TreatedAsUnset()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\napi_key = sk_api_file\n");
            Assert.Equal("sk_api_file", ApiKeyResolver.Resolve(null, "", configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MalformedFile_IsSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "not valid ini");
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FileWithoutApiKey_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\nother_key = value\n");
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CommentsAreIgnored()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "# comment\n[default]\n# another comment\napi_key = sk_api_comment\n");
            Assert.Equal("sk_api_comment", ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingDefaultSection_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[staging]\napi_key = sk_api_staging\n");
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "production"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DefaultSectionWithoutApiKey_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\nsome_other = value\n");
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UnreadableFile_CatchBlock_IsSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\napi_key = sk_api_test\n");
            // Inject a reader that throws to exercise the catch block.
            Assert.Throws<SmplException>(() =>
                ApiKeyResolver.Resolve(null, null, configPath, "test", _ => throw new IOException("simulated read error")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiKeyLineWithoutEquals_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            // "api_key" present but no '=' sign — eqIndex == -1 branch
            File.WriteAllText(configPath, "[default]\napi_key\n");
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApiKeyEqualsEmptyValue_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            // "api_key = " with empty value — value.Length == 0 branch
            File.WriteAllText(configPath, "[default]\napi_key = \n");
            Assert.Throws<SmplException>(() => ApiKeyResolver.Resolve(null, null, configPath, "test"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ------------------------------------------------------------------
    // Environment section takes precedence over [default]
    // ------------------------------------------------------------------

    [Fact]
    public void EnvironmentSection_TakesPrecedenceOverDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\napi_key = sk_default\n\n[staging]\napi_key = sk_staging\n");
            Assert.Equal("sk_staging", ApiKeyResolver.Resolve(null, null, configPath, "staging"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FallsBackToDefault_WhenEnvironmentSectionMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[default]\napi_key = sk_default\n");
            Assert.Equal("sk_default", ApiKeyResolver.Resolve(null, null, configPath, "production"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EnvironmentSection_UsedEvenWithoutDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".smplkit");
            File.WriteAllText(configPath, "[production]\napi_key = sk_prod\n");
            Assert.Equal("sk_prod", ApiKeyResolver.Resolve(null, null, configPath, "production"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
