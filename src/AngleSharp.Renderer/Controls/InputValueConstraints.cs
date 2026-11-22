namespace AngleSharp.Renderer.Controls;

using AngleSharp.Html.Dom;
using System.Globalization;

/// <summary>
/// What `min`, `max` and `step` allow as the value of a date or time input (HTML §4.10.5.3.8):
/// a value is allowed when it is within `min` and `max` and a whole number of steps away from
/// the step base (`min`, else the `value` attribute, else the default of the type). Steps are
/// days for `date`, weeks for `week`, months for `month` and seconds (60 by default) for `time`
/// and `datetime-local`; `step="any"` allows every value.
/// </summary>
public sealed class InputValueConstraints
{
    private const long MillisecondsPerDay = 86_400_000L;
    private static readonly long UnixEpochDay = new DateOnly(1970, 1, 1).DayNumber;

    private InputValueConstraints(string type, long? step, long stepBase, long? minimum, long? maximum)
    {
        Type = type;
        StepSize = step;
        StepBase = stepBase;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the type of the input, in lowercase.</summary>
    public string Type { get; }

    /// <summary>Gets if every value is a whole number of steps away from the base (no `step="any"`).</summary>
    public bool HasStep => StepSize is not null;

    // in the units of the type: days (date, week), months (month), milliseconds (time: of the
    // day, datetime-local: since 0001-01-01)
    internal long? StepSize { get; }

    internal long StepBase { get; }

    internal long? Minimum { get; }

    internal long? Maximum { get; }

    private bool IsTime => Type == "time";

    private bool IsDateTime => Type == "datetime-local";

    /// <summary>
    /// Gets the constraints of an input.
    /// </summary>
    /// <param name="input">The input element.</param>
    /// <returns>Its constraints.</returns>
    public static InputValueConstraints For(IHtmlInputElement input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var type = input.Type?.Trim().ToLowerInvariant() ?? "text";
        var minimum = ToUnits(type, input.GetAttribute("min"));
        var maximum = ToUnits(type, input.GetAttribute("max"));
        var (scale, defaultStep, defaultBase) = type switch
        {
            "week" => (7d, 7L, new DateOnly(1969, 12, 29).DayNumber),
            "month" => (1d, 1L, (1970L * 12L)),
            "time" => (1000d, 60_000L, 0L),
            "datetime-local" => (1000d, 60_000L, new DateTime(1970, 1, 1).Ticks / TimeSpan.TicksPerMillisecond),
            _ => (1d, 1L, UnixEpochDay),
        };
        var stepAttribute = input.GetAttribute("step")?.Trim();
        long? step = defaultStep;

        if (string.Equals(stepAttribute, "any", StringComparison.OrdinalIgnoreCase))
        {
            step = null;
        }
        else if (double.TryParse(stepAttribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0d && double.IsFinite(parsed))
        {
            // days, weeks and months are whole ones; times are milliseconds at least
            step = type is "time" or "datetime-local"
                ? Math.Max(1L, (long)Math.Round(parsed * scale))
                : Math.Max(1L, (long)Math.Round(parsed)) * (long)scale;
        }

        var stepBase = minimum ?? ToUnits(type, input.GetAttribute("value")) ?? defaultBase;
        return new InputValueConstraints(type, step, stepBase, minimum, maximum);
    }

    /// <summary>
    /// Gets if a day can be chosen: for `date` the day, for `week` its week, for `month` its
    /// month, for `datetime-local` any time of it.
    /// </summary>
    /// <param name="date">The day.</param>
    /// <returns>True if it is allowed.</returns>
    public bool Allows(DateOnly date)
    {
        switch (Type)
        {
            case "datetime-local":
            {
                var start = ToUnits(date, TimeOnly.MinValue);
                return AnyIn(start, start + MillisecondsPerDay);
            }

            case "time":
                return true;

            default:
                return Allows(ToUnits(date));
        }
    }

    /// <summary>
    /// Gets if a time is allowed (for `time`).
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>True if it is allowed.</returns>
    public bool Allows(TimeOnly time) => Allows(ToUnits(time));

    /// <summary>
    /// Gets if a local date and time is allowed (for `datetime-local`).
    /// </summary>
    /// <param name="dateTime">The date and time.</param>
    /// <returns>True if it is allowed.</returns>
    public bool Allows(DateTime dateTime) => Allows(dateTime.Ticks / TimeSpan.TicksPerMillisecond);

    /// <summary>
    /// Gets the allowed time nearest to a time (for `time`).
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>The allowed time, or null if no time is allowed.</returns>
    public TimeOnly? Snap(TimeOnly time) =>
        SnapInDay(null, ToUnits(time)) is { } snapped ? TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(snapped)) : null;

    /// <summary>
    /// Gets the allowed time of the same day nearest to a date and time (for `datetime-local`).
    /// </summary>
    /// <param name="dateTime">The date and time.</param>
    /// <returns>The allowed date and time, or null if no time of the day is allowed.</returns>
    public DateTime? Snap(DateTime dateTime)
    {
        var day = DateOnly.FromDateTime(dateTime);
        return SnapInDay(day, (long)dateTime.TimeOfDay.TotalMilliseconds) is { } snapped
            ? day.ToDateTime(TimeOnly.MinValue).AddMilliseconds(snapped)
            : null;
    }

    /// <summary>Gets if a time of a day (null for `time`) in [from, to) milliseconds of it is allowed.</summary>
    internal bool AnyInDay(DateOnly? day, long from, long to)
    {
        var offset = IsDateTime && day is { } date ? ToUnits(date, TimeOnly.MinValue) : 0L;

        if (IsTime && Minimum is { } min && Maximum is { } max && min > max)
        {
            // a range over midnight
            return AnyIn(Math.Max(from, min), to, ignoreRange: true) || AnyIn(from, Math.Min(to, max + 1), ignoreRange: true);
        }

        return AnyIn(offset + from, offset + to);
    }

    /// <summary>Gets the allowed time of a day (null for `time`) nearest to the given milliseconds of it.</summary>
    internal long? SnapInDay(DateOnly? day, long time)
    {
        var offset = IsDateTime && day is { } date ? ToUnits(date, TimeOnly.MinValue) : 0L;
        var value = offset + time;
        var step = StepSize ?? 1L;
        long? best = null;

        // the nearest allowed value is a step next to the time, or the step next to a bound
        // the time is beyond
        Span<long> candidates =
        [
            LastOnStep(value, step),
            FirstOnStep(value, step),
            Minimum is { } min ? FirstOnStep(min, step) : value,
            Maximum is { } max ? LastOnStep(max, step) : value,
        ];

        foreach (var candidate in candidates)
        {
            var inDay = candidate - offset;

            if (inDay >= 0L && inDay < MillisecondsPerDay && Allows(candidate) &&
                (best is null || Math.Abs(inDay - time) < Math.Abs(best.Value - time)))
            {
                best = inDay;
            }
        }

        return best;
    }

    /// <summary>
    /// Gets the allowed time of a day (null for `time`) in [from, to) milliseconds of it that is
    /// nearest to the given one, or null if none is.
    /// </summary>
    internal long? NearestInDay(DateOnly? day, long time, long from, long to)
    {
        var offset = IsDateTime && day is { } date ? ToUnits(date, TimeOnly.MinValue) : 0L;
        var step = StepSize ?? 1L;
        long? best = null;

        // the part of [from, to) within min and max, or the two parts of a range over midnight
        Span<(long From, long To)> parts = IsTime && Minimum is { } low && Maximum is { } high && low > high
            ? [(Math.Max(from, low), to), (from, Math.Min(to, high + 1L))]
            : [(Math.Max(offset + from, Minimum ?? long.MinValue) - offset, Math.Min(offset + to, Maximum is { } max ? max + 1L : long.MaxValue) - offset)];

        foreach (var (start, end) in parts)
        {
            if (start >= end)
            {
                continue;
            }

            var value = offset + Math.Clamp(time, start, end - 1L);
            Span<long> candidates = [LastOnStep(value, step), FirstOnStep(value, step)];

            foreach (var candidate in candidates)
            {
                var inDay = candidate - offset;

                if (inDay >= start && inDay < end && (best is null || Math.Abs(inDay - time) < Math.Abs(best.Value - time)))
                {
                    best = inDay;
                }
            }
        }

        return best;
    }

    /// <summary>Gets if a value in the units of the type is allowed.</summary>
    internal bool Allows(long value) =>
        InRange(value) && (StepSize is not { } step || FloorMod(value - StepBase, step) == 0L);

    private bool InRange(long value)
    {
        if (IsTime && Minimum is { } min && Maximum is { } max && min > max)
        {
            return value >= min || value <= max;
        }

        return (Minimum is not { } minimum || value >= minimum) && (Maximum is not { } maximum || value <= maximum);
    }

    // if a value in [from, to) is allowed
    private bool AnyIn(long from, long to, bool ignoreRange = false)
    {
        if (!ignoreRange)
        {
            from = Minimum is { } min ? Math.Max(from, min) : from;
            to = Maximum is { } max ? Math.Min(to, max + 1L) : to;
        }

        return from < to && FirstAtOrAfter(from) < to;
    }

    private long FirstAtOrAfter(long value) =>
        StepSize is { } step ? StepBase + (CeilDiv(value - StepBase, step) * step) : value;

    private long FirstOnStep(long value, long step) => StepBase + (CeilDiv(value - StepBase, step) * step);

    private long LastOnStep(long value, long step) => StepBase + (FloorDiv(value - StepBase, step) * step);

    private static long FloorDiv(long a, long b) => (a / b) - ((a % b != 0 && (a < 0) != (b < 0)) ? 1L : 0L);

    private static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

    private static long FloorMod(long a, long b) => a - (FloorDiv(a, b) * b);

    private long ToUnits(DateOnly date) => Type switch
    {
        "month" => (date.Year * 12L) + date.Month - 1L,
        "week" => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).DayNumber,
        _ => date.DayNumber,
    };

    private static long ToUnits(TimeOnly time) => time.Ticks / TimeSpan.TicksPerMillisecond;

    private static long ToUnits(DateOnly date, TimeOnly time) => date.ToDateTime(time).Ticks / TimeSpan.TicksPerMillisecond;

    private static long? ToUnits(string type, string? value) => type switch
    {
        "week" => InputValueFormat.TryParseWeek(value, out var monday) ? monday.DayNumber : null,
        "month" => InputValueFormat.TryParseMonth(value, out var month) ? (month.Year * 12L) + month.Month - 1L : null,
        "time" => InputValueFormat.TryParseTime(value, out var time) ? ToUnits(time) : null,
        "datetime-local" => InputValueFormat.TryParseDateTimeLocal(value, out var dateTime) ? dateTime.Ticks / TimeSpan.TicksPerMillisecond : null,
        _ => InputValueFormat.TryParseDate(value, out var date) ? date.DayNumber : null,
    };
}
