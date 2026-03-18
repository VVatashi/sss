using SimpleShadowsocks.Client.Core.Diagnostics;

namespace SimpleShadowsocks.Client.Tests.Unit.ClientCore.Diagnostics;

[Trait(TestCategories.Name, TestCategories.Unit)]
public sealed class FixedLogBufferTests
{
    [Fact]
    public void Add_KeepsInsertionOrder_WhileBelowCapacity()
    {
        var buffer = new FixedLogBuffer(capacity: 3);

        buffer.Add("first");
        buffer.Add("second");

        Assert.Equal(2, buffer.Count);
        Assert.Equal($"first{Environment.NewLine}second{Environment.NewLine}", buffer.BuildText());
    }

    [Fact]
    public void Add_DropsOldestMessage_WhenCapacityExceeded()
    {
        var buffer = new FixedLogBuffer(capacity: 3);

        buffer.Add("first");
        buffer.Add("second");
        buffer.Add("third");
        buffer.Add("fourth");

        Assert.Equal(3, buffer.Count);
        Assert.Equal(
            $"second{Environment.NewLine}third{Environment.NewLine}fourth{Environment.NewLine}",
            buffer.BuildText());
    }

    [Fact]
    public void Clear_RemovesAllMessages()
    {
        var buffer = new FixedLogBuffer(capacity: 2);

        buffer.Add("first");
        buffer.Add("second");
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(string.Empty, buffer.BuildText());
    }
}
