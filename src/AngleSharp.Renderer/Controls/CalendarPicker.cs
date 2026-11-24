namespace AngleSharp.Renderer.Controls;

using AngleSharp.Renderer.Rendering;
using System.Globalization;

/// <summary>
/// What a <see cref="CalendarPicker"/> chooses.
/// </summary>
public enum CalendarPickerMode
{
    /// <summary>A day (`date`).</summary>
    Date,

    /// <summary>A week, Monday to Sunday with its ISO number (`week`).</summary>
    Week,

    /// <summary>A month of a year (`month`).</summary>
    Month,

    /// <summary>A day and a time of it (`datetime-local`).</summary>
    DateTime,
}

/// <summary>
/// What is at a position of a <see cref="CalendarPicker"/>.
/// </summary>
public enum CalendarPickerPart
{
    /// <summary>Nothing to choose.</summary>
    None,

    /// <summary>The button showing the previous month (or year).</summary>
    Previous,

    /// <summary>The button showing the next month (or year).</summary>
    Next,

    /// <summary>A day, or a month, to choose.</summary>
    Cell,

    /// <summary>The button clearing the value.</summary>
    Clear,

    /// <summary>The button choosing today (this week, this month).</summary>
    Today,

    /// <summary>The columns choosing the time of a `datetime-local`.</summary>
    Time,
}

/// <summary>
/// The built-in picker of `date`, `week`, `month` and `datetime-local` inputs: a month of days
/// (weeks starting on Sunday, or numbered from Monday for `week`) or the months of a year, with
/// buttons for the previous and next ones, and Clear and Today. Choosing a day sets the value and
/// closes the picker; for `datetime-local` it stays open beside columns choosing the time.
/// Arrow keys move the focus (Page Up and Page Down by a month or a year), Enter chooses it and
/// Escape brings back the value the input had. `min` and `max` disable what is outside of them.
/// </summary>
public class CalendarPicker : InputPicker
{
    private const float Padding = 8f;
    private const int GridRows = 6;
    private const int MonthColumns = 4;
    private const int MonthRows = 3;

    private static readonly string[] SundayFirst = ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"];
    private static readonly string[] MondayFirst = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    private readonly float _cell;
    private readonly TimeColumns? _time;
    private DateOnly? _selected;
    private DateOnly _shown;
    private DateOnly _focus;
    private DateOnly? _hovered;

    /// <summary>
    /// Creates the picker of an input, showing its current value (else today).
    /// </summary>
    /// <param name="context">The context of the picker.</param>
    /// <param name="mode">What is chosen.</param>
    public CalendarPicker(InputPickerContext context, CalendarPickerMode mode)
        : base(context)
    {
        Mode = mode;
        Today = DateOnly.FromDateTime(context.Now);
        _cell = MathF.Round(Math.Max(28f, FontSize * 2.1f));
        _selected = Parse(context.Input.Value, out var time);
        Constraints = InputValueConstraints.For(context.Input);
        _focus = _selected ?? Normalize(Today);
        _shown = FirstShown(_focus);

        var calendarWidth = (Padding * 2f) + GridWidth;
        var width = calendarWidth;
        var height = Padding + _cell + GridHeight + Padding + _cell + Padding;

        if (mode == CalendarPickerMode.DateTime)
        {
            var columns = TimeColumns.ShowsSeconds(Constraints, _selected is null ? null : time) ? 4f : 3f;
            width += 1f + Padding + (columns * MathF.Round(Math.Max(MeasureText("00") + 16f, 36f))) + Padding;
        }

        Bounds = context.Place(width, height);

        if (mode == CalendarPickerMode.DateTime)
        {
            // as many whole rows as fit beside the calendar
            var left = Bounds.X + calendarWidth + 1f + Padding;
            var top = Bounds.Y + Padding;
            var rows = MathF.Floor((height - (2f * Padding)) / _cell);
            _time = new TimeColumns(new RenderRect(left, top, Bounds.X + width - Padding - left, rows * _cell), _cell, _selected is null ? null : time, Constraints, _selected ?? Today);
        }
    }

    /// <summary>Gets what is chosen.</summary>
    public CalendarPickerMode Mode { get; }

    /// <summary>Gets what `min`, `max` and `step` allow.</summary>
    public InputValueConstraints Constraints { get; }

    /// <summary>Gets today, as the context gave it.</summary>
    public DateOnly Today { get; }

    /// <summary>Gets the month shown (the first of the year, when choosing months).</summary>
    public DateOnly Shown => _shown;

    /// <summary>Gets the day (the Monday of the week, the first of the month) having the keyboard focus.</summary>
    public DateOnly Focus => _focus;

    /// <summary>Gets the chosen day (the Monday of the week, the first of the month), if any.</summary>
    public DateOnly? Selected => _selected;

    /// <summary>Gets the chosen time of a `datetime-local`, if any.</summary>
    public TimeOnly? Time => _time?.Time;

    private bool IsWeek => Mode == CalendarPickerMode.Week;

    private float GridLeft => Bounds.X + Padding + (IsWeek ? _cell : 0f);

    private float GridTop => Bounds.Y + Padding + _cell + (Mode == CalendarPickerMode.Month ? 0f : WeekdayHeight);

    private float WeekdayHeight => MathF.Round(_cell * 0.8f);

    private float GridWidth => (IsWeek ? 8f : 7f) * _cell;

    private float GridHeight => WeekdayHeight + (GridRows * _cell);

    private float CalendarRight => Bounds.X + Padding + GridWidth;

    private RenderRect Header => new(Bounds.X + Padding, Bounds.Y + Padding, GridWidth, _cell);

    private RenderRect PreviousButton => new(CalendarRight - (2f * _cell), Bounds.Y + Padding, _cell, _cell);

    private RenderRect NextButton => new(CalendarRight - _cell, Bounds.Y + Padding, _cell, _cell);

    private RenderRect Footer => new(Bounds.X + Padding, Bounds.Y + Padding + _cell + GridHeight + Padding, GridWidth, _cell);

    private RenderRect ClearButton => new(Footer.X, Footer.Y, MeasureText("Clear") + 16f, Footer.Height);

    private string TodayLabel => Mode switch
    {
        CalendarPickerMode.Week => "This week",
        CalendarPickerMode.Month => "This month",
        _ => "Today",
    };

    private RenderRect TodayButton
    {
        get
        {
            var width = MeasureText(TodayLabel) + 16f;
            return new RenderRect(Footer.X + Footer.Width - width, Footer.Y, width, Footer.Height);
        }
    }

    /// <summary>
    /// Gets what is at a position in the viewport.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="date">The day (the Monday of the week, the first of the month) of a <see cref="CalendarPickerPart.Cell"/>.</param>
    /// <returns>The part.</returns>
    public CalendarPickerPart GetPartAt(double x, double y, out DateOnly date)
    {
        date = default;

        if (!Contains(x, y))
        {
            return CalendarPickerPart.None;
        }

        if (Hit(PreviousButton, x, y))
        {
            return CalendarPickerPart.Previous;
        }

        if (Hit(NextButton, x, y))
        {
            return CalendarPickerPart.Next;
        }

        if (Hit(ClearButton, x, y))
        {
            return CalendarPickerPart.Clear;
        }

        if (Hit(TodayButton, x, y))
        {
            return CalendarPickerPart.Today;
        }

        if (_time is not null && Hit(_time.Area, x, y))
        {
            return CalendarPickerPart.Time;
        }

        if (Mode == CalendarPickerMode.Month)
        {
            var monthWidth = GridWidth / MonthColumns;
            var monthHeight = GridHeight / MonthRows;
            var column = (int)Math.Floor((x - Header.X) / monthWidth);
            var row = (int)Math.Floor((y - GridTop) / monthHeight);

            if (column is >= 0 and < MonthColumns && row is >= 0 and < MonthRows)
            {
                date = new DateOnly(_shown.Year, (row * MonthColumns) + column + 1, 1);
                return CalendarPickerPart.Cell;
            }

            return CalendarPickerPart.None;
        }

        var dayColumn = (int)Math.Floor((x - GridLeft) / _cell);
        var dayRow = (int)Math.Floor((y - GridTop) / _cell);

        // the number of a week chooses it as well
        if (IsWeek && dayColumn == -1)
        {
            dayColumn = 0;
        }

        if (dayColumn is >= 0 and < 7 && dayRow is >= 0 and < GridRows)
        {
            date = Normalize(GridStart.AddDays((dayRow * 7) + dayColumn));
            return CalendarPickerPart.Cell;
        }

        return CalendarPickerPart.None;
    }

    /// <summary>
    /// Gets where the cell of a day (or of its week or month) is, if it is shown.
    /// </summary>
    /// <param name="date">The day.</param>
    /// <returns>The bounds of its cell in the viewport, or null if it isn't shown.</returns>
    public RenderRect? GetCellBounds(DateOnly date)
    {
        if (Mode == CalendarPickerMode.Month)
        {
            if (date.Year != _shown.Year)
            {
                return null;
            }

            var monthWidth = GridWidth / MonthColumns;
            var monthHeight = GridHeight / MonthRows;
            var index = date.Month - 1;
            return new RenderRect(Header.X + ((index % MonthColumns) * monthWidth), GridTop + ((index / MonthColumns) * monthHeight), monthWidth, monthHeight);
        }

        var offset = date.DayNumber - GridStart.DayNumber;

        if (offset < 0 || offset >= GridRows * 7)
        {
            return null;
        }

        return new RenderRect(GridLeft + ((offset % 7) * _cell), GridTop + ((offset / 7) * _cell), _cell, _cell);
    }

    /// <inheritdoc />
    public override InputPickerResult PointerDown(double x, double y)
    {
        if (!IsOpen)
        {
            return InputPickerResult.None;
        }

        switch (GetPartAt(x, y, out var date))
        {
            case CalendarPickerPart.Previous:
                Show(Step(_shown, -1));
                return InputPickerResult.Repaint;
            case CalendarPickerPart.Next:
                Show(Step(_shown, 1));
                return InputPickerResult.Repaint;
            case CalendarPickerPart.Clear:
                return Choose(string.Empty);
            case CalendarPickerPart.Today:
                return Pick(Normalize(Today));
            case CalendarPickerPart.Cell when !IsDisabled(date):
                return Pick(date);
            case CalendarPickerPart.Time when _time!.Press(x, y):
                _selected ??= Normalize(Today);
                _focus = _selected.Value;
                SetValue(Format(_selected.Value));
                return InputPickerResult.Repaint;
            default:
                return InputPickerResult.None;
        }
    }

    /// <inheritdoc />
    public override InputPickerResult PointerMove(double x, double y)
    {
        if (!IsOpen)
        {
            return InputPickerResult.None;
        }

        DateOnly? hovered = GetPartAt(x, y, out var date) == CalendarPickerPart.Cell && !IsDisabled(date) ? date : null;

        if (hovered == _hovered)
        {
            return InputPickerResult.None;
        }

        _hovered = hovered;
        return InputPickerResult.Repaint;
    }

    /// <inheritdoc />
    public override InputPickerResult Wheel(double x, double y, double deltaY)
    {
        if (!IsOpen || deltaY == 0d)
        {
            return InputPickerResult.None;
        }

        if (_time is not null && Hit(_time.Area, x, y))
        {
            return _time.Scroll(x, y, deltaY) ? InputPickerResult.Repaint : InputPickerResult.None;
        }

        Show(Step(_shown, Math.Sign(deltaY)));
        return InputPickerResult.Repaint;
    }

    /// <inheritdoc />
    public override InputPickerResult KeyDown(string key)
    {
        if (!IsOpen)
        {
            return InputPickerResult.None;
        }

        var isMonth = Mode == CalendarPickerMode.Month;
        DateOnly? moved = key switch
        {
            "ArrowLeft" => isMonth ? _focus.AddMonths(-1) : _focus.AddDays(IsWeek ? -7 : -1),
            "ArrowRight" => isMonth ? _focus.AddMonths(1) : _focus.AddDays(IsWeek ? 7 : 1),
            "ArrowUp" => isMonth ? _focus.AddMonths(-MonthColumns) : _focus.AddDays(-7),
            "ArrowDown" => isMonth ? _focus.AddMonths(MonthColumns) : _focus.AddDays(7),
            "PageUp" => isMonth ? _focus.AddYears(-1) : _focus.AddMonths(-1),
            "PageDown" => isMonth ? _focus.AddYears(1) : _focus.AddMonths(1),
            "Home" => isMonth ? new DateOnly(_focus.Year, 1, 1) : new DateOnly(_focus.Year, _focus.Month, 1),
            _ => null,
        };

        if (moved is { } focus)
        {
            _focus = Normalize(focus);
            _shown = FirstShown(_focus);
            return InputPickerResult.Repaint;
        }

        if (key == "Enter" && !IsDisabled(_focus))
        {
            Pick(_focus);
            Close();
            return InputPickerResult.Close;
        }

        return base.KeyDown(key);
    }

    /// <inheritdoc />
    protected override void Paint(DisplayList displayList)
    {
        PaintFrame(displayList);

        var culture = CultureInfo.InvariantCulture;
        var title = Mode == CalendarPickerMode.Month ? _shown.ToString("yyyy", culture) : _shown.ToString("MMMM yyyy", culture);
        DrawText(displayList, title, Header.X + 4f, GetBaseline(Header), Palette.Text, 700f);
        PaintButton(displayList, PreviousButton, "‹", Palette.Text, framed: false);
        PaintButton(displayList, NextButton, "›", Palette.Text, framed: false);

        if (Mode == CalendarPickerMode.Month)
        {
            PaintMonths(displayList);
        }
        else
        {
            PaintDays(displayList);
        }

        PaintButton(displayList, ClearButton, "Clear", Palette.Accent, framed: true);
        PaintButton(displayList, TodayButton, TodayLabel, Palette.Accent, framed: true);

        if (_time is { } time)
        {
            displayList.FillRect(new RenderRect(CalendarRight + Padding, Bounds.Y + Padding, 1f, Bounds.Height - (2f * Padding)), Palette.Hover);
            time.Paint(displayList, Palette, (list, text, box, color) => DrawCenteredText(list, text, box, color), showFocus: false);
        }
    }

    /// <summary>
    /// Gets if a day (week, month) can't be chosen, as `min`, `max` and `step` don't allow it
    /// (for `datetime-local`: no time of it).
    /// </summary>
    /// <param name="date">The day (the Monday of the week, the first of the month).</param>
    /// <returns>True if it can't be chosen.</returns>
    public bool IsDisabled(DateOnly date) => !Constraints.Allows(date);

    private DateOnly GridStart
    {
        get
        {
            var first = new DateOnly(_shown.Year, _shown.Month, 1);
            var back = IsWeek ? ((int)first.DayOfWeek + 6) % 7 : (int)first.DayOfWeek;
            return first.AddDays(-back);
        }
    }

    private void PaintDays(DisplayList displayList)
    {
        var headers = IsWeek ? MondayFirst : SundayFirst;
        var weekdayRow = new RenderRect(GridLeft, GridTop - WeekdayHeight, _cell, WeekdayHeight);

        for (var column = 0; column < 7; column++)
        {
            DrawCenteredText(displayList, headers[column], weekdayRow with { X = GridLeft + (column * _cell) }, Palette.MutedText);
        }

        var start = GridStart;

        for (var row = 0; row < GridRows; row++)
        {
            var monday = start.AddDays(row * 7);
            var rowTop = GridTop + (row * _cell);

            if (IsWeek)
            {
                var week = ISOWeek.GetWeekOfYear(monday.ToDateTime(TimeOnly.MinValue));
                var weekRect = new RenderRect(GridLeft - _cell, rowTop, _cell, _cell);
                var rowRect = new RenderRect(weekRect.X + 2f, rowTop + 2f, (8f * _cell) - 4f, _cell - 4f);

                if (monday == _selected)
                {
                    displayList.FillRect(rowRect, Palette.Accent, Round(4f));
                }
                else if (monday == _hovered)
                {
                    displayList.FillRect(rowRect, Palette.Hover, Round(4f));
                }

                if (monday == _focus && _focus != _selected)
                {
                    displayList.StrokeRoundedRect(rowRect, Palette.Accent, 1f, Round(4f));
                }

                DrawCenteredText(displayList, week.ToString(CultureInfo.InvariantCulture), weekRect, monday == _selected ? Palette.AccentText : Palette.MutedText);
            }

            for (var column = 0; column < 7; column++)
            {
                var day = monday.AddDays(column);
                var cell = new RenderRect(GridLeft + (column * _cell), rowTop, _cell, _cell);
                var inner = new RenderRect(cell.X + 2f, cell.Y + 2f, cell.Width - 4f, cell.Height - 4f);
                var isSelected = IsWeek ? monday == _selected : day == _selected;
                var isDisabled = IsDisabled(Normalize(day));

                if (!IsWeek)
                {
                    if (isSelected)
                    {
                        displayList.FillRect(inner, Palette.Accent, Round(4f));
                    }
                    else if (day == _hovered)
                    {
                        displayList.FillRect(inner, Palette.Hover, Round(4f));
                    }

                    if (day == _focus && !isSelected)
                    {
                        displayList.StrokeRoundedRect(inner, Palette.Accent, 1f, Round(4f));
                    }
                }

                var color = isSelected ? Palette.AccentText
                    : isDisabled || day.Month != _shown.Month ? Palette.MutedText
                    : day == Today ? Palette.Accent
                    : Palette.Text;
                DrawCenteredText(displayList, day.Day.ToString(CultureInfo.InvariantCulture), cell, color, day == Today ? 700f : null);
            }
        }
    }

    private void PaintMonths(DisplayList displayList)
    {
        var monthWidth = GridWidth / MonthColumns;
        var monthHeight = GridHeight / MonthRows;
        var thisMonth = Normalize(Today);

        for (var index = 0; index < 12; index++)
        {
            var month = new DateOnly(_shown.Year, index + 1, 1);
            var cell = new RenderRect(Header.X + ((index % MonthColumns) * monthWidth), GridTop + ((index / MonthColumns) * monthHeight), monthWidth, monthHeight);
            var inner = new RenderRect(cell.X + 3f, cell.Y + 3f, cell.Width - 6f, cell.Height - 6f);
            var isSelected = month == _selected;

            if (isSelected)
            {
                displayList.FillRect(inner, Palette.Accent, Round(4f));
            }
            else if (month == _hovered)
            {
                displayList.FillRect(inner, Palette.Hover, Round(4f));
            }

            if (month == _focus && !isSelected)
            {
                displayList.StrokeRoundedRect(inner, Palette.Accent, 1f, Round(4f));
            }

            var color = isSelected ? Palette.AccentText : IsDisabled(month) ? Palette.MutedText : month == thisMonth ? Palette.Accent : Palette.Text;
            DrawCenteredText(displayList, month.ToString("MMM", CultureInfo.InvariantCulture), cell, color, month == thisMonth ? 700f : null);
        }
    }

    private void PaintButton(DisplayList displayList, RenderRect rect, string label, RenderColor color, bool framed)
    {
        var inner = new RenderRect(rect.X + 2f, rect.Y + 2f, rect.Width - 4f, rect.Height - 4f);

        if (framed)
        {
            displayList.FillRect(inner, Palette.Hover, Round(4f));
        }

        DrawCenteredText(displayList, label, rect, color, framed ? null : 700f);
    }

    private InputPickerResult Pick(DateOnly date)
    {
        if (IsDisabled(date))
        {
            return InputPickerResult.None;
        }

        _selected = date;
        _focus = date;
        _shown = FirstShown(date);

        if (Mode == CalendarPickerMode.DateTime && _time is not null)
        {
            // the time stays, moved to the nearest one the day allows
            if (Constraints.Snap(date.ToDateTime(_time.Time ?? TimeOnly.MinValue)) is not { } dateTime)
            {
                return InputPickerResult.None;
            }

            _time.Select(date, TimeOnly.FromDateTime(dateTime));
            SetValue(Format(date));
            return InputPickerResult.Repaint;
        }

        return Choose(Format(date));
    }

    private void Show(DateOnly shown)
    {
        _shown = shown;
        _hovered = null;
    }

    private DateOnly Step(DateOnly shown, int by) =>
        Mode == CalendarPickerMode.Month ? shown.AddYears(by) : shown.AddMonths(by);

    private DateOnly FirstShown(DateOnly date) =>
        Mode == CalendarPickerMode.Month ? new DateOnly(date.Year, 1, 1) : new DateOnly(date.Year, date.Month, 1);

    // what a cell stands for: the Monday of its week, the first of its month, or the day itself
    private DateOnly Normalize(DateOnly date) => Mode switch
    {
        CalendarPickerMode.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        CalendarPickerMode.Month => new DateOnly(date.Year, date.Month, 1),
        _ => date,
    };

    private DateOnly? Parse(string? value, out TimeOnly time)
    {
        time = default;

        switch (Mode)
        {
            case CalendarPickerMode.Week:
                return InputValueFormat.TryParseWeek(value, out var monday) ? monday : null;
            case CalendarPickerMode.Month:
                return InputValueFormat.TryParseMonth(value, out var month) ? month : null;
            case CalendarPickerMode.DateTime:
                if (InputValueFormat.TryParseDateTimeLocal(value, out var dateTime))
                {
                    time = TimeOnly.FromDateTime(dateTime);
                    return DateOnly.FromDateTime(dateTime);
                }

                return null;
            default:
                return InputValueFormat.TryParseDate(value, out var date) ? date : null;
        }
    }

    private string Format(DateOnly date) => Mode switch
    {
        CalendarPickerMode.Week => InputValueFormat.FormatWeek(date),
        CalendarPickerMode.Month => InputValueFormat.FormatMonth(date),
        CalendarPickerMode.DateTime => InputValueFormat.FormatDateTimeLocal(date.ToDateTime(_time?.Time ?? TimeOnly.MinValue)),
        _ => InputValueFormat.FormatDate(date),
    };
}
