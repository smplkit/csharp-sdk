using Smplkit.Flags;
using Xunit;

namespace Smplkit.Tests.Flags;

public class FlagRegistrationBufferTests
{
    // ------------------------------------------------------------------
    // Add + Drain (existing behaviour, kept for regression)
    // ------------------------------------------------------------------

    [Fact]
    public void Add_AddsEntry_DrainReturnsIt()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("dark-mode", "BOOLEAN", false, "api-gw", "production");
        var batch = buf.Drain();
        Assert.Single(batch);
        var e = batch[0];
        Assert.Equal("dark-mode", e.Id);
        Assert.Equal("BOOLEAN", e.Type);
        Assert.Equal(false, e.DefaultValue);
        Assert.Equal("api-gw", e.Service);
        Assert.Equal("production", e.Environment);
    }

    [Fact]
    public void Add_Deduplicates_ByIdFirstWins()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f", "BOOLEAN", false, "svc", "prod");
        buf.Add("f", "BOOLEAN", true, "other", "staging");
        var batch = buf.Drain();
        Assert.Single(batch);
        Assert.Equal(false, batch[0].DefaultValue);
    }

    [Fact]
    public void Drain_ClearsPending()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "STRING", "a", null, null);
        Assert.NotEmpty(buf.Drain());
        Assert.Empty(buf.Drain());
    }

    [Fact]
    public void PendingCount_IsAccurate()
    {
        var buf = new FlagRegistrationBuffer();
        Assert.Equal(0, buf.PendingCount);
        buf.Add("f1", "BOOLEAN", true, null, null);
        buf.Add("f2", "STRING", "x", null, null);
        Assert.Equal(2, buf.PendingCount);
        buf.Drain();
        Assert.Equal(0, buf.PendingCount);
    }

    // ------------------------------------------------------------------
    // Peek
    // ------------------------------------------------------------------

    [Fact]
    public void Peek_ReturnsSnapshot_DoesNotRemove()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, null, null);
        buf.Add("f2", "STRING", "x", null, null);

        var snapshot = buf.Peek();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, buf.PendingCount);
    }

    [Fact]
    public void Peek_MutatingSnapshot_DoesNotAffectBuffer()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, null, null);

        var snapshot = buf.Peek();
        snapshot.Clear();

        Assert.Equal(1, buf.PendingCount);
    }

    [Fact]
    public void Peek_EmptyBuffer_ReturnsEmpty()
    {
        var buf = new FlagRegistrationBuffer();
        Assert.Empty(buf.Peek());
    }

    // ------------------------------------------------------------------
    // Commit
    // ------------------------------------------------------------------

    [Fact]
    public void Commit_RemovesOnlySpecifiedIds()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, null, null);
        buf.Add("f2", "STRING", "x", null, null);

        buf.Commit(new[] { "f1" });

        var remaining = buf.Peek();
        Assert.Single(remaining);
        Assert.Equal("f2", remaining[0].Id);
    }

    [Fact]
    public void Commit_LeavesNewlyAddedItemsIntact()
    {
        // Simulates a flag declared between Peek and Commit (e.g. on another thread).
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, null, null);
        buf.Add("f2", "STRING", "x", null, null);

        var snapshot = buf.Peek();
        buf.Add("f3", "NUMERIC", 5.0, null, null);
        buf.Commit(snapshot.Select(e => e.Id));

        var remaining = buf.Peek();
        Assert.Single(remaining);
        Assert.Equal("f3", remaining[0].Id);
    }

    [Fact]
    public void Commit_EmptyIds_IsNoop()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, null, null);
        buf.Commit(Array.Empty<string>());
        Assert.Equal(1, buf.PendingCount);
    }

    [Fact]
    public void Commit_AllIds_ClearsPending()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, null, null);
        buf.Add("f2", "STRING", "a", null, null);
        buf.Commit(new[] { "f1", "f2" });
        Assert.Equal(0, buf.PendingCount);
    }

    // ------------------------------------------------------------------
    // Peek + Commit round-trip (mirrors the send path)
    // ------------------------------------------------------------------

    [Fact]
    public void PeekCommit_SuccessPath_RemovesItems()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, "svc", "prod");

        var batch = buf.Peek();
        Assert.Single(batch);

        // Simulate successful POST
        buf.Commit(batch.Select(e => e.Id));

        Assert.Equal(0, buf.PendingCount);
    }

    [Fact]
    public void PeekCommit_FailurePath_RetainsItems()
    {
        var buf = new FlagRegistrationBuffer();
        buf.Add("f1", "BOOLEAN", true, "svc", "prod");

        // Simulate: Peek, POST fails, no Commit
        var batch = buf.Peek();
        Assert.Single(batch);
        // (no Commit — simulating failure)

        Assert.Equal(1, buf.PendingCount);

        // On retry, Peek still returns the item
        var retryBatch = buf.Peek();
        Assert.Single(retryBatch);
        Assert.Equal("f1", retryBatch[0].Id);
    }
}
