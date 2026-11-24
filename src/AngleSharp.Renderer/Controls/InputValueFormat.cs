namespace AngleSharp.Renderer.Controls;

using System.Globalization;

/// <summary>
/// Parses and formats the values of the date and time inputs as HTML defines them (`2024-05-03`,
/// `13:05`, `2024-05-03T13:05`, `2024-05`, `2024-W18`).
/// </summary>
public static class InputValueFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFF"];

    /// <summary>Parses a date (`yyyy-MM-dd`).</summary>
    /// <param name="value">The value.</param>
    /// <param name="date">The date.</param>
    /// <returns>True if it is a valid date.</returns>
    public static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", Culture, DateTimeStyles.None, out date);

    /// <summary>Parses a time (`HH:mm`, with optional seconds and milliseconds).</summary>
    /// <param name="value">The value.</param>
    /// <param name="time">The time.</param>
    /// <returns>True if it is a valid time.</returns>
    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value?.Trim(), TimeFormats, Culture, DateTimeStyles.None, out time);

    /// <summary>Parses a local date and time (`yyyy-MM-ddTHH:mm`, also with a space).</summary>
    /// <param name="value">The value.</param>
    /// <param name="dateTime">The date and time.</param>
    /// <returns>True if it is a valid local date and time.</returns>
    public static bool TryParseDateTimeLocal(string? value, out DateTime dateTime)
    {
        dateTime = default;
        var trimmed = value?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOfAny(['T', ' ']);

        if (separator > 0 && TryParseDate(trimmed[..separator], out var date) && TryParseTime(trimmed[(separator + 1)..], out var time))
        {
            dateTime = date.ToDateTime(time);
            return true;
        }

        return false;
    }

    /// <summary>Parses a month (`yyyy-MM`) as its first day.</summary>
    /// <param name="value">The value.</param>
    /// <param name="month">The first day of the month.</param>
    /// <returns>True if it is a valid month.</returns>
    public static bool TryParseMonth(string? value, out DateOnly month) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM", Culture, DateTimeStyles.None, out month);

    /// <summary>Parses a week (`yyyy-Www`) as its Monday.</summary>
    /// <param name="value">The value.</param>
    /// <param name="monday">The Monday of the week.</param>
    /// <returns>True if it is a valid week.</returns>
    public static bool TryParseWeek(string? value, out DateOnly monday)
    {
        monday = default;
        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length == 8 && trimmed[4] == '-' && (trimmed[5] == 'W' || trimmed[5] == 'w') &&
            int.TryParse(trimmed.AsSpan(0, 4), NumberStyles.None, Culture, out var year) &&
            int.TryParse(trimmed.AsSpan(6, 2), NumberStyles.None, Culture, out var week) &&
            year >= 1 && week >= 1 && week <= ISOWeek.GetWeeksInYear(year))
        {
            monday = DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
            return true;
        }

        return false;
    }

    /// <summary>Formats a date.</summary>
    /// <param name="date">The date.</param>
    /// <returns>The value, e.g. `2024-05-03`.</returns>
    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", Culture);

    /// <summary>Formats a time, with seconds only if there are any.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The value, e.g. `13:05`.</returns>
    public static string FormatTime(TimeOnly time) =>
        time.ToString(time.Millisecond != 0 ? "HH:mm:ss.fff" : time.Second != 0 ? "HH:mm:ss" : "HH:mm", Culture);

    /// <summary>Formats a local date and time.</summary>
    /// <param name="dateTime">The date and time.</param>
    /// <returns>The value, e.g. `2024-05-03T13:05`.</returns>
    public static string FormatDateTimeLocal(DateTime dateTime) =>
        $"{FormatDate(DateOnly.FromDateTime(dateTime))}T{FormatTime(TimeOnly.FromDateTime(dateTime))}";

    /// <summary>Formats the month of a date.</summary>
    /// <param name="date">A day of the month.</param>
    /// <returns>The value, e.g. `2024-05`.</returns>
    public static string FormatMonth(DateOnly date) => date.ToString("yyyy-MM", Culture);

    /// <summary>Formats the ISO week of a date.</summary>
    /// <param name="date">A day of the week.</param>
    /// <returns>The value, e.g. `2024-W18`.</returns>
    public static string FormatWeek(DateOnly date)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);
        return string.Create(Culture, $"{ISOWeek.GetYear(dateTime):0000}-W{ISOWeek.GetWeekOfYear(dateTime):00}");
    }
}
