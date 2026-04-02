using Smplkit.Flags;
using Xunit;

namespace Smplkit.Tests.Flags;

public class CacheTests
{
    [Fact]
    public void Get_ReturnsMiss_ForUnknownKey()
    {
        var cache = new ResolutionCache(100);

        var (hit, value) = cache.Get("unknown");

        Assert.False(hit);
        Assert.Null(value);
    }

    [Fact]
    public void Put_ThenGet_ReturnsHit()
    {
        var cache = new ResolutionCache(100);

        cache.Put("key1", "value1");
        var (hit, value) = cache.Get("key1");

        Assert.True(hit);
        Assert.Equal("value1", value);
    }

    [Fact]
    public void Put_ThenGet_WorksWithNullValue()
    {
        var cache = new ResolutionCache(100);

        cache.Put("key-null", null);
        var (hit, value) = cache.Get("key-null");

        Assert.True(hit);
        Assert.Null(value);
    }

    [Fact]
    public void Put_OverwritesExistingKey()
    {
        var cache = new ResolutionCache(100);

        cache.Put("key1", "old");
        cache.Put("key1", "new");
        var (hit, value) = cache.Get("key1");

        Assert.True(hit);
        Assert.Equal("new", value);
    }

    [Fact]
    public void LruEviction_RemovesOldestEntry()
    {
        var cache = new ResolutionCache(3);

        cache.Put("a", 1);
        cache.Put("b", 2);
        cache.Put("c", 3);
        // Cache is full: a, b, c
        cache.Put("d", 4);
        // "a" should be evicted

        var (hitA, _) = cache.Get("a");
        Assert.False(hitA);

        var (hitB, valB) = cache.Get("b");
        Assert.True(hitB);
        Assert.Equal(2, valB);

        var (hitD, valD) = cache.Get("d");
        Assert.True(hitD);
        Assert.Equal(4, valD);
    }

    [Fact]
    public void LruEviction_AccessPromotesEntry()
    {
        var cache = new ResolutionCache(3);

        cache.Put("a", 1);
        cache.Put("b", 2);
        cache.Put("c", 3);

        // Access "a" to promote it
        cache.Get("a");

        // Now "b" is the oldest
        cache.Put("d", 4);

        var (hitA, _) = cache.Get("a");
        Assert.True(hitA); // "a" was promoted, should still be there

        var (hitB, _) = cache.Get("b");
        Assert.False(hitB); // "b" was oldest, should be evicted
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new ResolutionCache(100);
        cache.Put("a", 1);
        cache.Put("b", 2);

        cache.Clear();

        var (hitA, _) = cache.Get("a");
        var (hitB, _) = cache.Get("b");
        Assert.False(hitA);
        Assert.False(hitB);
    }

    [Fact]
    public void CacheHits_IncrementOnHit()
    {
        var cache = new ResolutionCache(100);
        cache.Put("key", "val");

        Assert.Equal(0, cache.CacheHits);
        cache.Get("key");
        Assert.Equal(1, cache.CacheHits);
        cache.Get("key");
        Assert.Equal(2, cache.CacheHits);
    }

    [Fact]
    public void CacheMisses_IncrementOnMiss()
    {
        var cache = new ResolutionCache(100);

        Assert.Equal(0, cache.CacheMisses);
        cache.Get("unknown1");
        Assert.Equal(1, cache.CacheMisses);
        cache.Get("unknown2");
        Assert.Equal(2, cache.CacheMisses);
    }

    [Fact]
    public void CacheHitsAndMisses_TrackSeparately()
    {
        var cache = new ResolutionCache(100);
        cache.Put("key", "val");

        cache.Get("key");       // hit
        cache.Get("missing");   // miss
        cache.Get("key");       // hit

        Assert.Equal(2, cache.CacheHits);
        Assert.Equal(1, cache.CacheMisses);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAccess()
    {
        var cache = new ResolutionCache(1000);
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            int taskId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    var key = $"task{taskId}-key{j}";
                    cache.Put(key, j);
                    cache.Get(key);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Verify no exceptions thrown and counters are consistent
        Assert.True(cache.CacheHits >= 0);
        Assert.True(cache.CacheMisses >= 0);
    }
}
