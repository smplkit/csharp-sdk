using Xunit;
using InternalHelpers = Smplkit.Internal.Helpers;

namespace Smplkit.Tests;

public class HelpersTests
{
    // ------------------------------------------------------------------
    // KeyToDisplayName
    // ------------------------------------------------------------------

    [Fact]
    public void KeyToDisplayName_KebabCase_TitleCasesWords()
    {
        Assert.Equal("Checkout V2", InternalHelpers.KeyToDisplayName("checkout-v2"));
    }

    [Fact]
    public void KeyToDisplayName_DotSeparated_TitleCasesWords()
    {
        Assert.Equal("Com Acme Payments", InternalHelpers.KeyToDisplayName("com.acme.payments"));
    }

    [Fact]
    public void KeyToDisplayName_SingleWord_TitleCases()
    {
        Assert.Equal("Simple", InternalHelpers.KeyToDisplayName("simple"));
    }

    [Fact]
    public void KeyToDisplayName_SnakeCase_TitleCasesWords()
    {
        Assert.Equal("Already Snake Case", InternalHelpers.KeyToDisplayName("already_snake_case"));
    }
}
