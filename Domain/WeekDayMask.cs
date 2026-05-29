namespace PillsReminderBot.Domain;

public static class WeekDayMask
{
    public const int Monday = 1;
    public const int Tuesday = 2;
    public const int Wednesday = 4;
    public const int Thursday = 8;
    public const int Friday = 16;
    public const int Saturday = 32;
    public const int Sunday = 64;

    public const int Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday;
    public const int AllDays = Weekdays | Saturday | Sunday;

    private static readonly (int Bit, DayOfWeek Day, string ShortName)[] Days =
    [
        (Monday, DayOfWeek.Monday, "Пн"),
        (Tuesday, DayOfWeek.Tuesday, "Вт"),
        (Wednesday, DayOfWeek.Wednesday, "Ср"),
        (Thursday, DayOfWeek.Thursday, "Чт"),
        (Friday, DayOfWeek.Friday, "Пт"),
        (Saturday, DayOfWeek.Saturday, "Сб"),
        (Sunday, DayOfWeek.Sunday, "Вс")
    ];

    public static IReadOnlyList<(int Bit, DayOfWeek Day, string ShortName)> OrderedDays => Days;

    public static int Normalize(int mask)
        => (mask & AllDays) == 0 ? AllDays : mask & AllDays;

    public static bool Contains(int mask, DayOfWeek day)
        => (Normalize(mask) & BitFor(day)) != 0;

    public static int Toggle(int mask, int bit)
    {
        bit &= AllDays;
        if (bit == 0)
            return Normalize(mask);

        return Normalize(mask) ^ bit;
    }

    public static string Format(int mask)
    {
        mask = Normalize(mask);

        return mask switch
        {
            AllDays => "ежедневно",
            Weekdays => "будни",
            _ => string.Join(", ", Days.Where(d => (mask & d.Bit) != 0).Select(d => d.ShortName))
        };
    }

    private static int BitFor(DayOfWeek day)
        => day switch
        {
            DayOfWeek.Monday => Monday,
            DayOfWeek.Tuesday => Tuesday,
            DayOfWeek.Wednesday => Wednesday,
            DayOfWeek.Thursday => Thursday,
            DayOfWeek.Friday => Friday,
            DayOfWeek.Saturday => Saturday,
            DayOfWeek.Sunday => Sunday,
            _ => AllDays
        };
}
