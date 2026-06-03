namespace RequestProcessor.Tests;

public class RequestPriorityTests
{
    // ── Enum values ───────────────────────────────────────────────────────────

    public static TheoryData<RequestPriority, int> PriorityIntValues => new()
    {
        { RequestPriority.Low,    0 },
        { RequestPriority.Normal, 1 },
        { RequestPriority.High,   2 },
    };

    [Theory, MemberData(nameof(PriorityIntValues))]
    public void Value_HasExpectedIntegerRepresentation(RequestPriority priority, int expected)
    {
        Assert.Equal(expected, (int)priority);
    }

    [Fact]
    public void ThreeDistinctValues_AreDefined()
    {
        var values = Enum.GetValues<RequestPriority>();
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void AllExpectedMembers_AreDefined()
    {
        var values = Enum.GetValues<RequestPriority>();
        Assert.Contains(RequestPriority.Low, values);
        Assert.Contains(RequestPriority.Normal, values);
        Assert.Contains(RequestPriority.High, values);
    }

    // ── Integer ordering guarantees (used by the pool's channel selection) ────

    [Fact]
    public void High_IsGreaterThan_Normal()
    {
        Assert.True(RequestPriority.High > RequestPriority.Normal,
            "High must be numerically greater than Normal for correct channel-priority semantics.");
    }

    [Fact]
    public void Normal_IsGreaterThan_Low()
    {
        Assert.True(RequestPriority.Normal > RequestPriority.Low,
            "Normal must be numerically greater than Low for correct channel-priority semantics.");
    }

    [Fact]
    public void High_IsGreaterThan_Low()
    {
        Assert.True(RequestPriority.High > RequestPriority.Low);
    }

    // ── IComparable / sort behaviour ─────────────────────────────────────────

    [Fact]
    public void Sorted_Ascending_ProducesLowNormalHigh()
    {
        var unsorted = new[] { RequestPriority.High, RequestPriority.Low, RequestPriority.Normal };
        Array.Sort(unsorted);
        Assert.Equal([RequestPriority.Low, RequestPriority.Normal, RequestPriority.High], unsorted);
    }

    [Fact]
    public void Sorted_Descending_ProducesHighNormalLow()
    {
        var unsorted = new[] { RequestPriority.Low, RequestPriority.High, RequestPriority.Normal };
        Array.Sort(unsorted, (a, b) => b.CompareTo(a));
        Assert.Equal([RequestPriority.High, RequestPriority.Normal, RequestPriority.Low], unsorted);
    }

    // ── Parse / roundtrip ─────────────────────────────────────────────────────

    public static TheoryData<RequestPriority, string> PriorityNames => new()
    {
        { RequestPriority.Low,    "Low"    },
        { RequestPriority.Normal, "Normal" },
        { RequestPriority.High,   "High"   },
    };

    [Theory, MemberData(nameof(PriorityNames))]
    public void ToString_ReturnsExpectedName(RequestPriority priority, string name)
    {
        // Name should match — also used in OTel tags.
        Assert.Equal(name, priority.ToString());
    }

    [Theory, MemberData(nameof(PriorityNames))]
    public void Parse_RoundTrips_FromString(RequestPriority expected, string name)
    {
        Assert.Equal(expected, Enum.Parse<RequestPriority>(name));
    }
}
