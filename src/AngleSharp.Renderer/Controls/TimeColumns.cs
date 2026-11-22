namespace AngleSharp.Renderer.Controls;

using AngleSharp.Renderer.Rendering;
using System.Globalization;
using System.Linq;

/// <summary>
/// A column of a time picker.
/// </summary>
public enum TimePickerColumn
{
    /// <summary>The hours, 01 to 12.</summary>
    Hour,

    /// <summary>The minutes.</summary>
    Minute,

    /// <summary>The seconds, shown when the step of the input is not a whole number of minutes.</summary>
    Second,

    /// <summary>AM (0) and PM (1).</summary>
    Meridiem,
}

/// <summary>
/// The columns of hours (01 to 12), minutes, seconds (if the step has any) and AM/PM that the
/// time pickers choose with, each scrolling on its own. A column only lists what `min`, `max`
/// and `step` allow at some time of the day; a chosen time is moved to the nearest allowed one.
/// </summary>
internal sealed class TimeColumns
{
    private const long Second = 1_000L;
    private const long Minute = 60_000L;
    private const long Hour = 3_600_000L;

    private readonly InputValueConstraints _constraints;
    private readonly TimePickerColumn[] _columns;
    private readonly Dictionary<TimePickerColumn, int> _scroll = new();
    private readonly Dictionary<TimePickerColumn, IReadOnlyList<int>> _values = new();

    public TimeColumns(RenderRect area, float rowHeight, TimeOnly? time, InputValueConstraints constraints, DateOnly? day)
    {
        Area = area;
        RowHeight = rowHeight;
        VisibleRows = Math.Max(1, (int)(area.Height / rowHeight));
        Time = time;
        Day = day;
        _constraints = constraints;
        _columns = ShowsSeconds(constraints, time)
            ? [TimePickerColumn.Hour, TimePickerColumn.Minute, TimePickerColumn.Second, TimePickerColumn.Meridiem]
            : [TimePickerColumn.Hour, TimePickerColumn.Minute, TimePickerColumn.Meridiem];
        Refresh(day);
    }

    public RenderRect Area { get; }

    public float RowHeight { get; }

    public int VisibleRows { get; }

    public TimeOnly? Time { get; private set; }

    /// <summary>Gets the day the times are of (for `datetime-local`), whose allowed times the columns list.</summary>
    public DateOnly? Day { get; private set; }

    public IReadOnlyList<TimePickerColumn> Columns => _columns;

    public TimePickerColumn FocusedColumn { get; private set; }

    public float ColumnWidth => Area.Width / _columns.Length;

    /// <summary>
    /// Gets if the columns of a time picker have seconds: when the step isn't a whole number of
    /// minutes (or its base has seconds), or without a step, when the time has seconds.
    /// </summary>
    public static bool ShowsSeconds(InputValueConstraints constraints, TimeOnly? time) =>
        constraints.StepSize is { } step
            ? step % Minute != 0L || ((constraints.StepBase % Minute) + Minute) % Minute >= Second
            : time is { } value && (value.Second != 0 || value.Millisecond != 0);

    public RenderRect? GetColumn(TimePickerColumn column)
    {
        var index = Array.IndexOf(_columns, column);
        return index < 0 ? null : new RenderRect(Area.X + (index * ColumnWidth), Area.Y, ColumnWidth, Area.Height);
    }

    /// <summary>Lists the times allowed on another day (for `datetime-local`).</summary>
    public void Refresh(DateOnly? day)
    {
        Day = day;

        foreach (var column in _columns)
        {
            _values[column] = ResolveValues(column);
            _scroll.TryAdd(column, 0);
            ScrollToSelected(column, center: true);
        }
    }

    /// <summary>Shows the times of another day (for `datetime-local`) with the given one chosen.</summary>
    public void Select(DateOnly? day, TimeOnly time)
    {
        Time = time;
        Refresh(day);
    }

    /// <summary>Chooses the row at the position; true if the time changed.</summary>
    public bool Press(double x, double y)
    {
        if (!TryGetRow(x, y, out var column, out var value))
        {
            return false;
        }

        FocusedColumn = column;
        return Set(column, value);
    }

    /// <summary>Gets the column and the value of the row at the position.</summary>
    public bool TryGetRow(double x, double y, out TimePickerColumn column, out int value)
    {
        column = default;
        value = -1;

        if (x < Area.X || x >= Area.X + Area.Width || y < Area.Y || y >= Area.Y + (VisibleRows * RowHeight))
        {
            return false;
        }

        column = _columns[Math.Clamp((int)((x - Area.X) / ColumnWidth), 0, _columns.Length - 1)];
        var row = _scroll[column] + (int)((y - Area.Y) / RowHeight);
        var values = _values[column];

        if (row >= values.Count)
        {
            return false;
        }

        value = values[row];
        return true;
    }

    /// <summary>Scrolls the column at the position; true if it moved.</summary>
    public bool Scroll(double x, double y, double deltaY)
    {
        if (x < Area.X || x >= Area.X + Area.Width || y < Area.Y || y >= Area.Y + Area.Height || deltaY == 0d)
        {
            return false;
        }

        var column = _columns[Math.Clamp((int)((x - Area.X) / ColumnWidth), 0, _columns.Length - 1)];
        var rows = Math.Max(1, (int)Math.Round(Math.Abs(deltaY) / RowHeight)) * Math.Sign(deltaY);
        var before = _scroll[column];
        _scroll[column] = Math.Clamp(before + rows, 0, MaximumScroll(column));
        return _scroll[column] != before;
    }

    /// <summary>Handles the arrow keys; true if the key was used.</summary>
    public bool Key(string key, out bool changed)
    {
        changed = false;
        var index = Array.IndexOf(_columns, FocusedColumn);

        switch (key)
        {
            case "ArrowLeft":
                FocusedColumn = _columns[Math.Max(0, index - 1)];
                return true;
            case "ArrowRight":
                FocusedColumn = _columns[Math.Min(_columns.Length - 1, index + 1)];
                return true;
            case "ArrowUp":
            case "ArrowDown":
            {
                var values = _values[FocusedColumn];

                if (values.Count == 0)
                {
                    return true;
                }

                var current = Time is null ? -1 : IndexOf(values, GetValue(FocusedColumn, Time.Value));
                var next = current < 0
                    ? (key == "ArrowUp" ? values.Count - 1 : 0)
                    : (current + (key == "ArrowUp" ? -1 : 1) + values.Count) % values.Count;
                changed = Set(FocusedColumn, values[next]);
                return true;
            }
            default:
                return false;
        }
    }

    public void Paint(DisplayList displayList, InputPickerPalette palette, Action<DisplayList, string, RenderRect, RenderColor> drawCentered, bool showFocus)
    {
        var radii = new RenderCornerRadii(3f, 3f, 3f, 3f, 3f, 3f, 3f, 3f);

        for (var index = 0; index < _columns.Length; index++)
        {
            var column = _columns[index];
            var rect = GetColumn(column)!.Value;

            if (index > 0)
            {
                displayList.FillRect(new RenderRect(rect.X, rect.Y, 1f, rect.Height), palette.Hover);
            }

            var values = _values[column];
            var selected = Time is { } time ? GetValue(column, time) : -1;

            for (var visible = 0; visible < VisibleRows; visible++)
            {
                var row = _scroll[column] + visible;

                if (row >= values.Count)
                {
                    break;
                }

                var rowRect = new RenderRect(rect.X + 2f, rect.Y + (visible * RowHeight) + 1f, rect.Width - 4f, RowHeight - 2f);
                var isSelected = values[row] == selected;

                if (isSelected)
                {
                    displayList.FillRect(rowRect, palette.Accent, radii);
                }

                drawCentered(displayList, GetLabel(column, values[row]), rowRect, isSelected ? palette.AccentText : palette.Text);
            }

            if (showFocus && column == FocusedColumn)
            {
                displayList.StrokeRoundedRect(new RenderRect(rect.X + 1f, rect.Y, rect.Width - 2f, VisibleRows * RowHeight), palette.Accent, 1f, radii);
            }
        }
    }

    // the time with one column set, moved to the nearest allowed time
    private bool Set(TimePickerColumn column, int value)
    {
        var time = Time ?? new TimeOnly(0, 0);
        var hour = time.Hour;
        var minute = time.Minute;
        var second = _columns.Contains(TimePickerColumn.Second) ? time.Second : 0;

        switch (column)
        {
            case TimePickerColumn.Hour:
                hour = (value % 12) + (hour >= 12 ? 12 : 0);
                break;
            case TimePickerColumn.Minute:
                minute = value;
                break;
            case TimePickerColumn.Second:
                second = value;
                break;
            default:
                hour = (hour % 12) + (value == 1 ? 12 : 0);
                break;
        }

        var candidate = (hour * Hour) + (minute * Minute) + (second * Second);

        // what was chosen stays if any allowed time has it, else the time moves to the nearest one
        var (from, to) = column switch
        {
            TimePickerColumn.Hour => (hour * Hour, (hour + 1) * Hour),
            TimePickerColumn.Minute => ((hour * Hour) + (minute * Minute), (hour * Hour) + ((minute + 1) * Minute)),
            TimePickerColumn.Second => (candidate, candidate + Second),
            _ => (hour >= 12 ? 12 * Hour : 0L, hour >= 12 ? 24 * Hour : 12 * Hour),
        };

        if ((_constraints.NearestInDay(Day, candidate, from, to) ?? _constraints.SnapInDay(Day, candidate)) is not { } snapped)
        {
            return false;
        }

        var updated = TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(snapped));
        var changed = Time != updated;
        Time = updated;

        foreach (var each in _columns)
        {
            ScrollToSelected(each, center: false);
        }

        return changed;
    }

    // the values of a column allowed at some time of the day
    private IReadOnlyList<int> ResolveValues(TimePickerColumn column)
    {
        bool Any(long from, long to) => _constraints.AnyInDay(Day, from, to);

        switch (column)
        {
            case TimePickerColumn.Hour:
                return Enumerable.Range(1, 12).Where(hour12 => Any((hour12 % 12) * Hour, ((hour12 % 12) + 1) * Hour) || Any(((hour12 % 12) + 12) * Hour, ((hour12 % 12) + 13) * Hour)).ToList();
            case TimePickerColumn.Minute:
                return Enumerable.Range(0, 60).Where(minute => Enumerable.Range(0, 24).Any(hour => Any((hour * Hour) + (minute * Minute), (hour * Hour) + ((minute + 1) * Minute)))).ToList();
            case TimePickerColumn.Second:
                return Enumerable.Range(0, 60).Where(second =>
                    Enumerable.Range(0, 1440).Any(minute => Any((minute * Minute) + (second * Second), (minute * Minute) + ((second + 1) * Second)))).ToList();
            default:
                return Enumerable.Range(0, 2).Where(meridiem => Any(meridiem * 12 * Hour, (meridiem + 1) * 12 * Hour)).ToList();
        }
    }

    private static int GetValue(TimePickerColumn column, TimeOnly time) => column switch
    {
        TimePickerColumn.Hour => time.Hour % 12 == 0 ? 12 : time.Hour % 12,
        TimePickerColumn.Minute => time.Minute,
        TimePickerColumn.Second => time.Second,
        _ => time.Hour >= 12 ? 1 : 0,
    };

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private int MaximumScroll(TimePickerColumn column) => Math.Max(0, _values[column].Count - VisibleRows);

    private void ScrollToSelected(TimePickerColumn column, bool center)
    {
        var maximum = MaximumScroll(column);
        var row = Time is { } time ? IndexOf(_values[column], GetValue(column, time)) : -1;

        if (row < 0)
        {
            _scroll[column] = Math.Min(_scroll[column], maximum);
        }
        else if (center)
        {
            _scroll[column] = Math.Clamp(row - (VisibleRows / 2), 0, maximum);
        }
        else if (row < _scroll[column])
        {
            _scroll[column] = row;
        }
        else if (row >= _scroll[column] + VisibleRows)
        {
            _scroll[column] = Math.Min(maximum, row - VisibleRows + 1);
        }
    }

    private static string GetLabel(TimePickerColumn column, int value) => column switch
    {
        TimePickerColumn.Meridiem => value == 0 ? "AM" : "PM",
        _ => value.ToString("00", CultureInfo.InvariantCulture),
    };
}
