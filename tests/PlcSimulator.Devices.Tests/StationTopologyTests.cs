using PlcSimulator.Core.Configuration;
using PlcSimulator.Devices.Topology;

namespace PlcSimulator.Devices.Tests;

/// <summary>
/// 拓扑构建与寻路。用现场一条真实路径验证：
/// 1001(x=30) → 10021(x=29，提升机) → 1004(x=28) → 1010(x=27) → 1015(x=26) → 1017(x=25)，
/// 全部 arrowdirection = 3（向左）、同一 zonecode。
/// </summary>
public class StationTopologyTests
{
    private const string Zone = "输送机监控";

    private static StationConfig Station(
        string no,
        int x,
        int y = 21,
        string arrow = "3",
        string remark = "",
        string field5 = "",
        int width = 1,
        int height = 1,
        string name = "")
        => new()
        {
            StationNo = no,
            Name = string.IsNullOrEmpty(name) ? no : name,
            ByteOffset = x * 2,
            LengthBytes = 30,
            LocationX = x,
            LocationY = y,
            Width = width,
            Height = height,
            ArrowDirection = arrow,
            ZoneCode = Zone,
            Remark = remark,
            Field5 = field5,
        };

    private static StationTopology BuildLine() => StationTopology.Build(
    [
        Station("1001", 30, remark: "异常口"),
        Station("10021", 29, remark: "提升机"),
        Station("1004", 28),
        Station("1010", 27),
        Station("1015", 26),
        Station("1017", 25, remark: "扫码"),
    ]);

    [Theory]
    [InlineData("1001", "1017", "10021")]
    [InlineData("10021", "1017", "1004")]
    [InlineData("1004", "1017", "1010")]
    [InlineData("1010", "1017", "1015")]
    [InlineData("1015", "1017", "1017")]
    public void TryGetNextHop_OnRealLine_ReturnsAdjacentStation(string from, string to, string expected)
    {
        StationTopology topology = BuildLine();

        Assert.True(topology.TryGetNextHop(from, to, out string next));
        Assert.Equal(expected, next);
    }

    [Fact]
    public void TryGetNextHop_AlreadyAtTarget_ReturnsFalse()
    {
        StationTopology topology = BuildLine();

        Assert.False(topology.TryGetNextHop("1017", "1017", out _));
    }

    [Fact]
    public void TryGetNextHop_NoTopology_ReturnsFalse()
    {
        // 站台没有 arrowdirection / 坐标时建不出邻接表，调用方应退回直接投送
        StationTopology topology = StationTopology.Build([new StationConfig { StationNo = "1001" }]);

        Assert.False(topology.TryGetNextHop("1001", "1004", out _));
    }

    [Fact]
    public void Build_StationsWithArrowZero_AreExcluded()
    {
        StationTopology topology = StationTopology.Build(
        [
            Station("1001", 30, arrow: "0"),      // 不参与拓扑
            Station("10021", 29),
            Station("1004", 28),
        ]);

        Assert.False(topology.Contains("1001"));
        Assert.True(topology.Contains("10021"));
        Assert.False(topology.TryGetNextHop("1001", "1004", out _));
    }

    [Fact]
    public void Build_StationsWithField5One_AreExcluded()
    {
        StationTopology topology = StationTopology.Build(
        [
            Station("1001", 30),
            Station("10021", 29, field5: "1"),    // 不参与拓扑
            Station("1004", 28),
        ]);

        Assert.False(topology.Contains("10021"));
        Assert.False(topology.TryGetNextHop("1001", "1004", out _));
    }

    [Fact]
    public void Build_LifterGroup_CollapsesIntoOneNode()
    {
        // 10021 / 10022 同为提升机、同前 4 位 → 塌缩成一个节点，代表站台取第一个
        StationTopology topology = StationTopology.Build(
        [
            Station("1001", 30),
            Station("10021", 29, remark: "提升机"),
            Station("10022", 29, remark: "提升机"),
            Station("1004", 28),
        ]);

        Assert.True(topology.TryGetNextHop("1001", "1004", out string next));
        Assert.Equal("10021", next);
    }

    [Fact]
    public void Build_Lifters_AreGroupedByStationNoPrefix_ItemNameIsIgnored()
    {
        // 归组只看**站台号前 4 位**：itemname 允许重复、只用于显示，不作依据。
        // 10021 与 10022 前缀同为 "1002" → 同一台提升机的两层，即使 itemname 各不相同也照样合并，
        // 出边取两层的并集（10021 那层连 1004，10022 那层连 1018）。
        StationTopology topology = StationTopology.Build(
        [
            Station("1001", 30, remark: "异常口"),
            Station("10021", 29, remark: "提升机", name: "1002-1"),
            Station("10022", 25, y: 19, arrow: "2", remark: "提升机", name: "1019-1"),
            Station("1004", 28),
            Station("1018", 25, y: 20, arrow: "2"),
            Station("1017", 25),
        ]);

        Assert.Equal(["1004", "1018"], topology.NeighborsOf("10021"));
        Assert.Equal(["1004", "1018"], topology.NeighborsOf("10022"));   // 同节点：看到同一组出边
    }

    [Fact]
    public void Build_DifferentZone_IsNotAdjacent()
    {
        var other = Station("1004", 28);
        other.ZoneCode = "另一区域";

        StationTopology topology = StationTopology.Build([Station("1001", 30), other]);

        Assert.False(topology.TryGetNextHop("1001", "1004", out _));
    }

    [Fact]
    public void Build_ReversedDirection_DoesNotLinkBackwards()
    {
        // 1004 方向为向右（4），则它的下一站应在 x=29 一侧、而不是继续向左
        StationTopology topology = StationTopology.Build(
        [
            Station("1001", 30),
            Station("10021", 29),
            Station("1004", 28, arrow: "4"),
        ]);

        Assert.True(topology.TryGetNextHop("1004", "10021", out string next));
        Assert.Equal("10021", next);
    }

    [Fact]
    public void TryGetPathByCost_AllZeroCost_SameAsPlainBfs()
    {
        StationTopology topology = BuildLine();

        Assert.True(topology.TryGetPathByCost(
            "1001", "1017", static (_, _) => 0, out IReadOnlyList<string> path, out int cost));

        Assert.Equal(0, cost);
        Assert.True(topology.TryGetPath("1001", "1017", out IReadOnlyList<string> plain));
        Assert.Equal(plain, path);
    }

    [Fact]
    public void TryGetPathByCost_AllExpensive_StillFindsRouteAndCountsCost()
    {
        // 每条边都要「补方向」时仍应找到路径，总代价就是跳数。
        StationTopology topology = BuildLine();

        Assert.True(topology.TryGetPathByCost(
            "1001", "1017", static (_, _) => 1, out IReadOnlyList<string> path, out int cost));

        Assert.Equal(path.Count - 1, cost);
    }

    [Fact]
    public void TryGetPathByCost_NoRoute_ReturnsFalse()
    {
        StationTopology topology = StationTopology.Build(
        [
            Station("1001", 30),
            Station("1010", 5, y: 5),
        ]);

        Assert.False(topology.TryGetPathByCost(
            "1001", "1010", static (_, _) => 0, out _, out _));
    }
}
