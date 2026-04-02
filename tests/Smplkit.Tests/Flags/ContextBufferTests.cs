using Smplkit.Flags;
using Xunit;

namespace Smplkit.Tests.Flags;

public class ContextBufferTests
{
    [Fact]
    public void Observe_AddsToPending()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        var ctx = new Context("user", "user-1", new Dictionary<string, object?> { ["plan"] = "pro" });

        buffer.Observe(new[] { ctx });

        Assert.Equal(1, buffer.PendingCount);
    }

    [Fact]
    public void Observe_DeduplicatesByTypeAndKey()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        var ctx1 = new Context("user", "user-1", new Dictionary<string, object?> { ["plan"] = "pro" });
        var ctx2 = new Context("user", "user-1", new Dictionary<string, object?> { ["plan"] = "enterprise" });

        buffer.Observe(new[] { ctx1 });
        buffer.Observe(new[] { ctx2 });

        Assert.Equal(1, buffer.PendingCount);
    }

    [Fact]
    public void Observe_DifferentKeysNotDeduplicated()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        var ctx1 = new Context("user", "user-1");
        var ctx2 = new Context("user", "user-2");

        buffer.Observe(new[] { ctx1, ctx2 });

        Assert.Equal(2, buffer.PendingCount);
    }

    [Fact]
    public void Observe_DifferentTypesNotDeduplicated()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        var ctx1 = new Context("user", "id-1");
        var ctx2 = new Context("account", "id-1");

        buffer.Observe(new[] { ctx1, ctx2 });

        Assert.Equal(2, buffer.PendingCount);
    }

    [Fact]
    public void Drain_ReturnsAndClearsPending()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        buffer.Observe(new[]
        {
            new Context("user", "u1"),
            new Context("account", "a1"),
        });

        var batch = buffer.Drain();

        Assert.Equal(2, batch.Count);
        Assert.Equal(0, buffer.PendingCount);

        // Verify the structure of drained items
        Assert.Equal("user:u1", batch[0]["id"]);
        Assert.Equal("u1", batch[0]["name"]); // no Name set, falls back to Key
        Assert.Equal("account:a1", batch[1]["id"]);
    }

    [Fact]
    public void Drain_WithNameSet_UsesName()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        buffer.Observe(new[] { new Context("user", "u1", name: "Alice") });

        var batch = buffer.Drain();

        Assert.Single(batch);
        Assert.Equal("Alice", batch[0]["name"]);
    }

    [Fact]
    public void Drain_EmptyBuffer_ReturnsEmptyList()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);

        var batch = buffer.Drain();

        Assert.Empty(batch);
    }

    [Fact]
    public void PendingCount_IsAccurate()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);

        Assert.Equal(0, buffer.PendingCount);

        buffer.Observe(new[] { new Context("user", "u1") });
        Assert.Equal(1, buffer.PendingCount);

        buffer.Observe(new[] { new Context("user", "u2"), new Context("account", "a1") });
        Assert.Equal(3, buffer.PendingCount);

        buffer.Drain();
        Assert.Equal(0, buffer.PendingCount);
    }

    [Fact]
    public void LruEviction_WhenExceedingMaxSeenSize()
    {
        // LRU size of 3
        var buffer = new ContextRegistrationBuffer(3, 50);

        buffer.Observe(new[]
        {
            new Context("user", "u1"),
            new Context("user", "u2"),
            new Context("user", "u3"),
        });

        // Drain so pending is clear but seen set is full
        buffer.Drain();

        // Add a 4th context -- this should evict u1 from seen
        buffer.Observe(new[] { new Context("user", "u4") });
        Assert.Equal(1, buffer.PendingCount);

        buffer.Drain();

        // Now u1 is no longer in seen, so re-observing it should add to pending
        buffer.Observe(new[] { new Context("user", "u1") });
        Assert.Equal(1, buffer.PendingCount);

        // u2 might also be evicted, but u3/u4 should still be in seen
        // u4 was most recently added, u3 was before that
        buffer.Observe(new[] { new Context("user", "u4") });
        Assert.Equal(1, buffer.PendingCount); // u4 already seen, not added again
    }

    [Fact]
    public void Observe_CopiesAttributes()
    {
        var buffer = new ContextRegistrationBuffer(100, 50);
        var attrs = new Dictionary<string, object?> { ["plan"] = "pro" };
        buffer.Observe(new[] { new Context("user", "u1", attrs) });

        var batch = buffer.Drain();
        var batchAttrs = (Dictionary<string, object?>)batch[0]["attributes"]!;

        // Modify original -- should not affect the drained copy
        attrs["plan"] = "free";
        Assert.Equal("pro", batchAttrs["plan"]);
    }
}
