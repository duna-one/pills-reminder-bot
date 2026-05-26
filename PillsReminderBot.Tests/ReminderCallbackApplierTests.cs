using PillsReminderBot.Bot;
using PillsReminderBot.Domain;

namespace PillsReminderBot.Tests;

public sealed class ReminderCallbackApplierTests
{
    [Fact]
    public void TrySnooze_StaleCycle_ReturnsFalseAndDoesNotChangeReminder()
    {
        var originalNextFireAt = new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero);
        var reminder = new Reminder
        {
            AwaitingAck = true,
            ActiveCycleId = "current-cycle",
            NextFireAtUtc = originalNextFireAt,
            UpdatedAtUtc = new DateTimeOffset(2026, 5, 27, 8, 0, 0, TimeSpan.Zero)
        };

        var applied = ReminderCallbackApplier.TrySnooze(
            reminder,
            cycleId: "old-cycle",
            nowUtc: new DateTimeOffset(2026, 5, 27, 8, 15, 0, TimeSpan.Zero),
            snoozeMinutes: 30);

        Assert.False(applied);
        Assert.True(reminder.AwaitingAck);
        Assert.Equal("current-cycle", reminder.ActiveCycleId);
        Assert.Equal(originalNextFireAt, reminder.NextFireAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 5, 27, 8, 0, 0, TimeSpan.Zero), reminder.UpdatedAtUtc);
    }

    [Fact]
    public void TrySnooze_CurrentCycle_PostponesReminderAndKeepsAwaitingAck()
    {
        var nowUtc = new DateTimeOffset(2026, 5, 27, 8, 15, 0, TimeSpan.Zero);
        var reminder = new Reminder
        {
            AwaitingAck = true,
            ActiveCycleId = "current-cycle",
            NextFireAtUtc = new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero)
        };

        var applied = ReminderCallbackApplier.TrySnooze(reminder, "current-cycle", nowUtc, 30);

        Assert.True(applied);
        Assert.True(reminder.AwaitingAck);
        Assert.Equal("current-cycle", reminder.ActiveCycleId);
        Assert.Equal(nowUtc.AddMinutes(30), reminder.NextFireAtUtc);
        Assert.Equal(nowUtc, reminder.UpdatedAtUtc);
        Assert.Null(reminder.LastAcknowledgedAtUtc);
    }
}
