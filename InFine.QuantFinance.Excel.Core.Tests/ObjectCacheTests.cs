using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

public class ObjectCacheTests
{
    private sealed class Curve { }

    private sealed class Surface { }

    [Fact]
    public void HandlesCarryKindNameAndVersion()
    {
        var cache = new ObjectCache();
        string handle = cache.Put("QF.Curve", "EUR6M", new Curve());
        Assert.Equal("QF.Curve:EUR6M#1", handle);
        Assert.True(ObjectCache.TryParseHandle(handle, out var kind, out var name, out var version));
        Assert.Equal(("QF.Curve", "EUR6M", 1), (kind, name, version));
    }

    [Fact]
    public void ReplacingAnObjectBumpsTheVersionSoDependentCellsSeeANewHandle()
    {
        var cache = new ObjectCache();
        var first = new Curve();
        var second = new Curve();
        string h1 = cache.Put("QF.Curve", "EUR6M", first);
        string h2 = cache.Put("QF.Curve", "EUR6M", second);
        Assert.Equal("QF.Curve:EUR6M#1", h1);
        Assert.Equal("QF.Curve:EUR6M#2", h2);
        Assert.Same(second, cache.Get<Curve>(h2));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AStaleHandleIsRejectedNotSilentlyResolvedToTheNewObject()
    {
        var cache = new ObjectCache();
        string old = cache.Put("QF.Curve", "EUR6M", new Curve());
        string current = cache.Put("QF.Curve", "EUR6M", new Curve());
        var e = Assert.Throws<XlException>(() => cache.Get<Curve>(old));
        Assert.Equal(XlError.Ref, e.Error);
        Assert.Contains("stale", e.Message);
        Assert.Contains("#2", e.Message);
        Assert.NotNull(cache.Get<Curve>(current));
        Assert.False(cache.TryGet<Curve>(old, out _));
    }

    [Fact]
    public void IdenticalInputsKeepTheExistingVersion()
    {
        var cache = new ObjectCache();
        string h1 = cache.Put("QF.Curve", "C", new Curve(), fingerprint: "flat|0.03");
        string h2 = cache.Put("QF.Curve", "C", new Curve(), fingerprint: "flat|0.03");
        string h3 = cache.Put("QF.Curve", "C", new Curve(), fingerprint: "flat|0.04");
        string h4 = cache.Put("QF.Curve", "C", new Curve());   // no fingerprint: always a new version
        Assert.Equal(h1, h2);
        Assert.Equal("QF.Curve:C#2", h3);
        Assert.Equal("QF.Curve:C#3", h4);
    }

    [Fact]
    public void SameNameUnderAnotherKindIsAnotherObject()
    {
        var cache = new ObjectCache();
        string curve = cache.Put("QF.Curve", "EUR", new Curve());
        string surface = cache.Put("QF.Surface", "EUR", new Surface());
        Assert.NotEqual(curve, surface);
        Assert.Equal(2, cache.Count);
        Assert.IsType<Curve>(cache.Get<Curve>(curve));
        Assert.IsType<Surface>(cache.Get<Surface>(surface));
    }

    [Fact]
    public void GettingTheWrongTypeNamesBothTypes()
    {
        var cache = new ObjectCache();
        string handle = cache.Put("QF.Curve", "EUR6M", new Curve());
        var e = Assert.Throws<XlException>(() => cache.Get<Surface>(handle));
        Assert.Equal(XlError.Value, e.Error);
        Assert.Contains("Curve", e.Message);
        Assert.Contains("Surface", e.Message);
        Assert.Contains(handle, e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EUR6M")]
    [InlineData("QF.Curve:EUR6M")]
    [InlineData("QF.Curve:EUR6M#")]
    [InlineData("QF.Curve:EUR6M#x")]
    [InlineData("QF.Curve:EUR6M#0")]
    [InlineData(":EUR6M#1")]
    [InlineData("QF.Curve:#1")]
    public void MalformedHandlesAreValueErrors(string text)
    {
        var cache = new ObjectCache();
        var e = Assert.Throws<XlException>(() => cache.Get<Curve>(text));
        Assert.Equal(XlError.Value, e.Error);
    }

    [Fact]
    public void UnknownHandlesAreRefErrors()
    {
        var cache = new ObjectCache();
        var e = Assert.Throws<XlException>(() => cache.Get<Curve>("QF.Curve:NOPE#1"));
        Assert.Equal(XlError.Ref, e.Error);
        Assert.Contains("QF.Curve:NOPE", e.Message);
    }

    [Fact]
    public void DeleteRemovesOnlyTheCurrentVersionAndVersionsKeepCounting()
    {
        var cache = new ObjectCache();
        string h1 = cache.Put("QF.Curve", "C", new Curve());
        string h2 = cache.Put("QF.Curve", "C", new Curve());
        Assert.False(cache.Delete(h1));          // stale: must not delete the newer object
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Delete(h2));
        Assert.False(cache.Delete(h2));          // already gone
        Assert.False(cache.Delete("garbage"));
        Assert.Equal(0, cache.Count);
        Assert.Equal(XlError.Ref, Assert.Throws<XlException>(() => cache.Get<Curve>(h2)).Error);

        string h3 = cache.Put("QF.Curve", "C", new Curve());
        Assert.Equal("QF.Curve:C#3", h3);        // a deleted handle can never match a later object
        Assert.Equal(XlError.Ref, Assert.Throws<XlException>(() => cache.Get<Curve>(h2)).Error);
    }

    [Fact]
    public void ListAndInfoDescribeLiveObjects()
    {
        var cache = new ObjectCache();
        cache.Put("QF.Surface", "S", new Surface(), description: "a surface");
        string curve = cache.Put("QF.Curve", "A", new Curve(), description: "flat",
            details: new[] { new KeyValuePair<string, object>("Rate", 0.03) });
        var list = cache.List();
        Assert.Equal(new[] { "QF.Curve:A#1", "QF.Surface:S#1" }, list.Select(i => i.Handle));
        var info = cache.Info(curve);
        Assert.Equal(("QF.Curve", "A", 1, "Curve", "flat"), (info.Kind, info.Name, info.Version, info.TypeName, info.Description));
        Assert.Equal(0.03, info.Details.Single().Value);
        Assert.InRange((DateTime.UtcNow - info.CreatedUtc).TotalSeconds, -1, 60);
        cache.Clear();
        Assert.Empty(cache.List());
    }

    [Fact]
    public void RejectsUnusableNames()
    {
        var cache = new ObjectCache();
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => cache.Put("QF.Curve", "", new Curve())).Error);
        Assert.Equal(XlError.Value, Assert.Throws<XlException>(() => cache.Put("QF.Curve", "a#b", new Curve())).Error);
        Assert.Throws<ArgumentException>(() => cache.Put("QF:Curve", "a", new Curve()));
        // A colon inside the name is fine: the first colon ends the kind.
        string handle = cache.Put("QF.Curve", "EUR:6M", new Curve());
        Assert.NotNull(cache.Get<Curve>(handle));
    }

    [Fact]
    public void ConcurrentUseKeepsEveryHandleResolvableAndVersionsUnique()
    {
        var cache = new ObjectCache();
        var versions = new ConcurrentBag<int>();
        const int workers = 8, iterations = 500;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            for (int i = 0; i < iterations; i++)
            {
                // Everybody replaces the same name (contention on one key) and also owns a private one.
                string shared = cache.Put("QF.Curve", "shared", new Curve());
                ObjectCache.TryParseHandle(shared, out _, out _, out int v);
                versions.Add(v);
                string mine = cache.Put("QF.Curve", "w" + worker, new Curve());
                // The private handle is only ever replaced by its owner, so it must resolve.
                Assert.NotNull(cache.Get<Curve>(mine));
                // The shared handle may already be stale, but resolving it must fail cleanly, never corrupt the cache.
                if (!cache.TryGet<Curve>(shared, out var found)) Assert.Null(found);
                _ = cache.List();
                if (i % 50 == 0) cache.Delete(mine);
            }
        });
        Assert.Equal(workers * iterations, versions.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, workers * iterations), versions.OrderBy(v => v));
        Assert.True(cache.Count <= workers + 1);
    }
}
