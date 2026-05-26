using PillsReminderBot.Bot;

namespace PillsReminderBot.Tests;

public sealed class ReminderCallbackDataTests
{
    [Fact]
    public void TryParse_AckPayload_ReturnsAckData()
    {
        var parsed = ReminderCallbackData.TryParse("ack:42:cycle-1", out var data);

        Assert.True(parsed);
        Assert.NotNull(data);
        Assert.Equal(ReminderCallbackAction.Acknowledge, data.Action);
        Assert.Equal(42, data.ReminderId);
        Assert.Equal("cycle-1", data.CycleId);
        Assert.Null(data.SnoozeMinutes);
    }

    [Fact]
    public void TryParse_SnoozePayload_ReturnsSnoozeData()
    {
        var parsed = ReminderCallbackData.TryParse("snooze:42:cycle-1:30", out var data);

        Assert.True(parsed);
        Assert.NotNull(data);
        Assert.Equal(ReminderCallbackAction.Snooze, data.Action);
        Assert.Equal(42, data.ReminderId);
        Assert.Equal("cycle-1", data.CycleId);
        Assert.Equal(30, data.SnoozeMinutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ack")]
    [InlineData("ack:not-id:cycle")]
    [InlineData("ack:42:")]
    [InlineData("snooze:42:cycle")]
    [InlineData("snooze:42:cycle:not-minutes")]
    [InlineData("snooze:42:cycle:0")]
    [InlineData("unknown:42:cycle")]
    public void TryParse_InvalidPayload_ReturnsFalse(string payload)
    {
        var parsed = ReminderCallbackData.TryParse(payload, out var data);

        Assert.False(parsed);
        Assert.Null(data);
    }
}
