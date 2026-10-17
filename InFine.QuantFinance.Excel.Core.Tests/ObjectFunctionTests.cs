using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace InFine.QuantFinance.Excel.Core.Tests;

[Collection("QuantFunctions")]
public class ObjectFunctionTests
{
    private readonly ObjectCache _cache = new();
    private readonly QuantFunctions _qf;

    public ObjectFunctionTests()
    {
        _qf = new QuantFunctions(_cache);
    }

    [Fact]
    public void ListOfAnEmptyCacheIsJustTheHeaderRow()
    {
        var list = T.Array(_qf.ObjList());
        Assert.Equal(1, list.GetLength(0));
        Assert.Equal("Handle", list[0, 0]);
    }

    [Fact]
    public void ListShowsOneRowPerLiveObject()
    {
        string curve = T.Text(_qf.CurveFlat("EUR", 45000.0, 0.02, "ACT/365F"));
        string surface = T.Text(_qf.SviFit("SPX", 1.0, 100.0,
            T.Column(80.0, 90.0, 100.0, 110.0, 120.0), T.Column(0.28, 0.24, 0.21, 0.22, 0.25)));
        var list = T.Array(_qf.ObjList());
        Assert.Equal(3, list.GetLength(0));
        Assert.Equal(7, list.GetLength(1));
        Assert.Equal(curve, list[1, 0]);
        Assert.Equal("QF.Curve", list[1, 1]);
        Assert.Equal("EUR", list[1, 2]);
        Assert.Equal(1.0, list[1, 3]);
        Assert.Equal("FlatForward", list[1, 4]);
        Assert.Equal(surface, list[2, 0]);
        Assert.Equal("SviSmile", list[2, 4]);
    }

    [Fact]
    public void InfoIsAKeyValueTable()
    {
        string handle = T.Text(_qf.CurveFlat("EUR", 45000.0, 0.02, "ACT/365F"));
        var info = T.Array(_qf.ObjInfo(handle));
        Assert.Equal(2, info.GetLength(1));
        var map = Enumerable.Range(0, info.GetLength(0)).ToDictionary(i => (string)info[i, 0], i => info[i, 1]);
        Assert.Equal(handle, map["Handle"]);
        Assert.Equal("QF.Curve", map["Kind"]);
        Assert.Equal(1.0, map["Version"]);
        Assert.Equal("Actual/365 (Fixed)", map["Day count"]);
        Assert.Equal(0.02, map["Rate"]);
        Assert.Equal("2023-03-15", map["Reference date"]);   // serial 45000
    }

    [Fact]
    public void InfoOfAStaleOrUnknownHandleIsAnError()
    {
        string old = T.Text(_qf.CurveFlat("EUR", 45000.0, 0.02, "ACT/365F"));
        _qf.CurveFlat("EUR", 45000.0, 0.03, "ACT/365F");
        Assert.Equal(XlError.Ref, _qf.ObjInfo(old));
        Assert.Equal(XlError.Value, _qf.ObjInfo("hello"));
        Assert.Equal(XlError.Value, _qf.ObjInfo(null));
    }

    [Fact]
    public void DeleteReturnsWhetherSomethingWasRemoved()
    {
        string handle = T.Text(_qf.CurveFlat("EUR", 45000.0, 0.02, "ACT/365F"));
        Assert.Equal(true, _qf.ObjDelete(handle));
        Assert.Equal(false, _qf.ObjDelete(handle));
        Assert.Equal(false, _qf.ObjDelete("nonsense"));
        Assert.Equal(XlError.Ref, _qf.CurveDf(handle, 45100.0));
        Assert.Equal(1, T.Array(_qf.ObjList()).GetLength(0));
        // Re-creating after a delete yields a higher version, never the deleted handle again.
        Assert.Equal("QF.Curve:EUR#2", T.Text(_qf.CurveFlat("EUR", 45000.0, 0.02, "ACT/365F")));
    }

    [Fact]
    public void ConcurrentFunctionCallsAreSafe()
    {
        // The Excel shim marks everything thread-safe; hammer constructors and readers from many threads.
        Parallel.For(0, 200, i =>
        {
            string name = "C" + (i % 5);
            string handle = T.Text(_qf.CurveFlat(name, 45000.0, 0.01 + 0.001 * (i % 3), "ACT/365F"));
            object df = _qf.CurveDf(handle, 45365.0);
            // Another thread may have replaced the curve in between: the only acceptable failure is a stale #REF!.
            Assert.True(df is double || (df is XlError e && e == XlError.Ref), $"unexpected result {df}");
            Assert.IsType<object[,]>(_qf.ObjList());
            Assert.Equal(10.4506, T.Num(_qf.BsPrice("C", 100.0, 100.0, 1.0, 0.05, 0.0, 0.2)), 4);
        });
        Assert.InRange(_cache.Count, 1, 5);
    }
}
