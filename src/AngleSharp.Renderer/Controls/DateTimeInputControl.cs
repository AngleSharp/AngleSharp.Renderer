namespace AngleSharp.Renderer.Controls;

using AngleSharp.Renderer.Rendering;

/// <summary>
/// The built-in control of the date and time inputs (`date`, `datetime-local`, `month`, `time`,
/// `week`): the value as en-US browsers show it (`05/03/2024`, `01:05 PM`, `May 2024`,
/// `Week 18, 2024`, or the placeholder of the type) beside a calendar or clock icon. Its picker is
/// a <see cref="CalendarPicker"/> (with the times of a day for `datetime-local`) or, for `time`,
/// a <see cref="TimePicker"/>. Derive from it to change what is shown.
/// </summary>
public class DateTimeInputControl : IInputControl
{
    /// <summary>Gets the input types the control is for.</summary>
    public static IReadOnlyList<string> Types { get; } = ["date", "datetime-local", "month", "time", "week"];

    /// <summary>
    /// Creates the control of an input type.
    /// </summary>
    /// <param name="type">One of <see cref="Types"/>.</param>
    public DateTimeInputControl(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type.Trim().ToLowerInvariant();
    }

    /// <summary>Gets the input type of the control.</summary>
    public string Type { get; }

    /// <summary>
    /// Formats a value for showing it in the input.
    /// </summary>
    /// <param name="value">The value of the input.</param>
    /// <returns>The text to show.</returns>
    public virtual string FormatValue(string value) => HtmlRenderer.FormatDateTimeInputValue(Type, value);

    /// <inheritdoc />
    /// <remarks>
    /// Wide enough for the widest value of its fields (or the shown value, if longer), the padding
    /// of those fields and the picker icon with its margin; a line high (the clock of a time
    /// input is a bit taller).
    /// </remarks>
    public virtual InputControlSize? GetIntrinsicSize(InputControlSizeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (widestValue, fieldCount) = Type switch
        {
            "time" => ("--:-- AM", 3),
            "datetime-local" => ("mm/dd/yyyy, --:-- AM", 6),
            "month" => ("September ------", 2),
            "week" => ("Week --, ------", 2),
            _ => ("mm/dd/yyyy", 3),
        };
        var fontSize = context.Font.FontSize;
        var textWidth = Math.Max(context.MeasureText(widestValue), context.MeasureText(FormatValue(context.Value)));
        var width = textWidth + (fieldCount * 2f) + HtmlRenderer.FormControlPickerIconMargin + (fontSize * 1.2f);
        var height = Type == "time" ? Math.Max(context.LineHeight, (fontSize * 1.2f) + 4f) : context.LineHeight + 2f;
        return new InputControlSize(width, height);
    }

    /// <inheritdoc />
    public virtual void Paint(InputControlPaintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var box = context.ContentBox;
        context.DrawText(FormatValue(context.Value), box.X, context.TextBaseline);

        var iconSize = Math.Min(box.Height, context.Font.FontSize * HtmlRenderer.FormControlPickerIconScale);
        var iconRect = new RenderRect(box.X + box.Width - iconSize, box.Y + ((box.Height - iconSize) / 2f), iconSize, iconSize);
        PaintIcon(context.DisplayList, iconRect, context.TextColor, Type == "time");
    }

    /// <inheritdoc />
    public virtual IInputPicker? CreatePicker(InputPickerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Type switch
        {
            "time" => new TimePicker(context),
            "month" => new CalendarPicker(context, CalendarPickerMode.Month),
            "week" => new CalendarPicker(context, CalendarPickerMode.Week),
            "datetime-local" => new CalendarPicker(context, CalendarPickerMode.DateTime),
            _ => new CalendarPicker(context, CalendarPickerMode.Date),
        };
    }

    /// <summary>
    /// Paints the picker icon: a calendar (an outlined page with a filled header) or a clock (a
    /// circle with two hands).
    /// </summary>
    /// <param name="displayList">The list to paint into.</param>
    /// <param name="rect">Where the icon goes.</param>
    /// <param name="color">Its color.</param>
    /// <param name="isClock">True for the clock.</param>
    public static void PaintIcon(DisplayList displayList, RenderRect rect, RenderColor color, bool isClock)
    {
        ArgumentNullException.ThrowIfNull(displayList);
        HtmlRenderer.PaintPickerIcon(displayList, rect, color, isClock);
    }
}
