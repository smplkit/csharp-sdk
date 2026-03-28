using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

public class AuthTests
{
    [Fact]
    public void ApplyBearerToken_SetsAuthorizationHeader()
    {
        using var httpClient = new HttpClient();

        Auth.ApplyBearerToken(httpClient, "sk_test_key_123");

        var auth = httpClient.DefaultRequestHeaders.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("sk_test_key_123", auth.Parameter);
    }

    [Fact]
    public void ApplyBearerToken_OverwritesPreviousToken()
    {
        using var httpClient = new HttpClient();

        Auth.ApplyBearerToken(httpClient, "old_key");
        Auth.ApplyBearerToken(httpClient, "new_key");

        var auth = httpClient.DefaultRequestHeaders.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("new_key", auth.Parameter);
    }
}
