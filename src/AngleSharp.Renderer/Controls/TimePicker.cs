namespace AngleSharp.Renderer.Controls;

using AngleSharp.Renderer.Rendering;

/// <summary>
/// The built-in picker of `time` inputs: columns of hours, minutes, seconds (when the `step` has
/// any) and AM/PM, scrolled by the wheel. The columns only list what `min`, `max` and `step`
/// allow, and a chosen time is moved to the nearest allowed one. Choosing a row sets the value at
/// once; Enter or a press outside closes the picker, Escape brings back the value it had.
/// </summary>
public class TimePicker : InputPicker
{
    private const float Padding = 6f;
    private const int VisibleRows = 7;

    private readonly TimeColumns _columns;

    /// <summary>
    /// Creates the picker of a time input, showing its current value.
    /// </summary>
    /// <param name="context">The context of the picker.</param>
    public TimePicker(InputPickerContext context)
        : base(context)
    {
        Constraints = InputValueConstraints.For(context.Input);
        TimeOnly? time = InputValueFormat.TryParseTime(context.Input.Value, out var parsed) ? parsed : null;
        var columns = TimeColumns.ShowsSeconds(Constraints, time) ? 4 : 3;
        var rowHeight = MathF.Round(Math.Max(FontSize * 1.8f, 24f));
        var columnWidth = MathF.Round(Math.Max(MeasureText("00") + 24f, 44f));
        var width = (columns * columnWidth) + (2f * Padding);
        var height = (VisibleRows * rowHeight) + (2f * Padding);
        Bounds = context.Place(width, height);
        _columns = new TimeColumns(
            new RenderRect(Bounds.X + Padding, Bounds.Y + Padding, columns * columnWidth, VisibleRows * rowHeight),
            rowHeight,
            time,
            Constraints,
            null);
    }

    /// <summary>Gets what `min`, `max` and `step` allow.</summary>
    public InputValueConstraints Constraints { get; }

    /// <summary>Gets the chosen time, if any.</summary>
    public TimeOnly? Time => _columns.Time;

    /// <summary>Gets the height of a row.</summary>
    public float RowHeight => _columns.RowHeight;

    /// <summary>Gets the columns shown, from left to right.</summary>
    public IReadOnlyList<TimePickerColumn> Columns => _columns.Columns;

    /// <summary>
    /// Gets the bounds of a column.
    /// </summary>
    /// <param name="column">The column.</param>
    /// <returns>Its bounds in the viewport, or null if it isn't shown.</returns>
    public RenderRect? GetColumnBounds(TimePickerColumn column) => _columns.GetColumn(column);

    /// <summary>
    /// Gets the row at a position in the viewport.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="column">The column of the row.</param>
    /// <param name="value">The hour (1 to 12), the minute, the second, or 0 for AM and 1 for PM.</param>
    /// <returns>True if there is a row at the position.</returns>
    public bool TryGetRowAt(double x, double y, out TimePickerColumn column, out int value) =>
        _columns.TryGetRow(x, y, out column, out value);

    /// <inheritdoc />
    public override InputPickerResult PointerDown(double x, double y)
    {
        if (!IsOpen || !_columns.TryGetRow(x, y, out _, out _))
        {
            return InputPickerResult.None;
        }

        _columns.Press(x, y);
        Apply();
        return InputPickerResult.Repaint;
    }

    /// <inheritdoc />
    public override InputPickerResult Wheel(double x, double y, double deltaY) =>
        IsOpen && _columns.Scroll(x, y, deltaY) ? InputPickerResult.Repaint : InputPickerResult.None;

    /// <inheritdoc />
    public override InputPickerResult KeyDown(string key)
    {
        if (IsOpen && _columns.Key(key, out var changed))
        {
            if (changed)
            {
                Apply();
            }

            return InputPickerResult.Repaint;
        }

        return base.KeyDown(key);
    }

    /// <inheritdoc />
    protected override void Paint(DisplayList displayList)
    {
        PaintFrame(displayList);
        _columns.Paint(displayList, Palette, (list, text, box, color) => DrawCenteredText(list, text, box, color), showFocus: true);
    }

    private void Apply()
    {
        if (_columns.Time is { } time)
        {
            SetValue(InputValueFormat.FormatTime(time));
        }
    }
}
