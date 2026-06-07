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

    // ------------------------------------------------------------------
    // FetchAllPagesAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task FetchAllPagesAsync_SinglePage_StopsAfterShortBatch()
    {
        var calls = new List<(int Page, int Size)>();
        var result = await InternalHelpers.FetchAllPagesAsync<int>(
            (page, size, _) =>
            {
                calls.Add((page, size));
                return Task.FromResult(new List<int> { 1, 2, 3 });
            });

        Assert.Single(calls);
        Assert.Equal(1, calls[0].Page);
        Assert.Equal(InternalHelpers.RuntimePageSize, calls[0].Size);
        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public async Task FetchAllPagesAsync_EmptyFirstPage_StopsAfterOneCall()
    {
        var callCount = 0;
        var result = await InternalHelpers.FetchAllPagesAsync<int>(
            (_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new List<int>());
            });

        Assert.Equal(1, callCount);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchAllPagesAsync_FullFirstPage_FetchesSecondPage()
    {
        var calls = new List<int>();
        var full = Enumerable.Range(0, InternalHelpers.RuntimePageSize).ToList();
        var tail = new List<int> { 9001, 9002 };

        var result = await InternalHelpers.FetchAllPagesAsync<int>(
            (page, _, _) =>
            {
                calls.Add(page);
                return Task.FromResult(page == 1 ? full : tail);
            });

        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.Equal(InternalHelpers.RuntimePageSize + 2, result.Count);
        Assert.Equal(9002, result[^1]);
    }

    [Fact]
    public async Task FetchAllPagesAsync_TwoFullPagesThenShort_StopsOnShort()
    {
        var calls = new List<int>();
        var full = Enumerable.Range(0, InternalHelpers.RuntimePageSize).ToList();

        var result = await InternalHelpers.FetchAllPagesAsync<int>(
            (page, _, _) =>
            {
                calls.Add(page);
                return Task.FromResult(page < 3 ? full : new List<int> { 42 });
            });

        Assert.Equal(new[] { 1, 2, 3 }, calls);
        Assert.Equal(2 * InternalHelpers.RuntimePageSize + 1, result.Count);
    }

    [Fact]
    public async Task FetchAllPagesAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            InternalHelpers.FetchAllPagesAsync<int>(
                (_, _, c) => Task.FromCanceled<List<int>>(c), cts.Token));
    }

    // ------------------------------------------------------------------
    // JoinEnvironments
    // ------------------------------------------------------------------

    [Fact]
    public void JoinEnvironments_Null_ReturnsNull()
    {
        Assert.Null(InternalHelpers.JoinEnvironments(null));
    }

    [Fact]
    public void JoinEnvironments_Empty_ReturnsNull()
    {
        Assert.Null(InternalHelpers.JoinEnvironments(Array.Empty<string>()));
    }

    [Fact]
    public void JoinEnvironments_SingleValue_ReturnsValue()
    {
        Assert.Equal("production", InternalHelpers.JoinEnvironments(new[] { "production" }));
    }

    [Fact]
    public void JoinEnvironments_MultipleValues_JoinsWithComma()
    {
        Assert.Equal(
            "production,staging",
            InternalHelpers.JoinEnvironments(new[] { "production", "staging" }));
    }
}
