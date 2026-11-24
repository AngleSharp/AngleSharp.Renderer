namespace AngleSharp.Renderer.Controls;

using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using System.Globalization;
using System.Linq;

/// <summary>
/// The built-in picker of `color` inputs: an area choosing saturation (across) and brightness
/// (down), a hue slider, the chosen color with its hex and RGB values, and swatches to choose
/// from (the suggestions of the input's `list`, else a few common colors). The value follows the
/// pointer while it is dragged, firing `input`; `change` is fired when the picker closes.
/// </summary>
public class ColorPicker : InputPicker
{
    private const float Padding = 10f;
    private const float PickerWidth = 232f;
    private const float AreaHeight = 128f;
    private const float Gap = 10f;
    private const float PreviewSize = 24f;
    private const float HueBarHeight = 12f;
    private const float SwatchSize = 20f;
    private const float SwatchGap = 6f;
    private const float KeyStep = 0.01f;

    private static readonly RenderColor[] DefaultSwatches =
    [
        new(0, 0, 0), new(255, 255, 255), new(234, 67, 53), new(251, 188, 5),
        new(52, 168, 83), new(66, 133, 244), new(161, 66, 244), new(255, 109, 182),
    ];

    private readonly IReadOnlyList<RenderColor> _swatches;
    private readonly List<RenderRect> _swatchRects = [];
    private float _hue;
    private float _saturation;
    private float _brightness;
    private DragTarget _drag;

    private enum DragTarget
    {
        None,
        Area,
        Hue,
    }

    /// <summary>
    /// Creates the picker of a color input, showing its current value.
    /// </summary>
    /// <param name="context">The context of the picker.</param>
    public ColorPicker(InputPickerContext context)
        : base(context)
    {
        _swatches = ResolveSwatches(context.Input);
        var color = TryParseSimpleColor(context.Input.Value, out var parsed) ? parsed : RenderColor.Black;
        (_hue, _saturation, _brightness) = ToHsv(color);

        var lineHeight = Math.Max(FontSize * 1.5f, 20f);
        var innerWidth = PickerWidth - (2f * Padding);
        var perRow = Math.Max(1, (int)((innerWidth + SwatchGap) / (SwatchSize + SwatchGap)));
        var swatchRows = (_swatches.Count + perRow - 1) / perRow;
        var swatchesHeight = swatchRows == 0 ? 0f : (swatchRows * SwatchSize) + ((swatchRows - 1) * SwatchGap) + Gap;
        var height = Padding + AreaHeight + Gap + PreviewSize + Gap + lineHeight + swatchesHeight + Padding;
        Bounds = context.Place(PickerWidth, height);

        var x = Bounds.X + Padding;
        var y = Bounds.Y + Padding;
        Area = new RenderRect(x, y, innerWidth, AreaHeight);
        y += AreaHeight + Gap;
        Preview = new RenderRect(x, y, PreviewSize, PreviewSize);
        HueBar = new RenderRect(x + PreviewSize + Gap, y + ((PreviewSize - HueBarHeight) / 2f), innerWidth - PreviewSize - Gap, HueBarHeight);
        y += PreviewSize + Gap;
        ValueLine = new RenderRect(x, y, innerWidth, lineHeight);
        y += lineHeight + Gap;

        for (var index = 0; index < _swatches.Count; index++)
        {
            var row = index / perRow;
            var column = index % perRow;
            _swatchRects.Add(new RenderRect(x + (column * (SwatchSize + SwatchGap)), y + (row * (SwatchSize + SwatchGap)), SwatchSize, SwatchSize));
        }
    }

    /// <summary>Gets the area choosing saturation and brightness.</summary>
    public RenderRect Area { get; }

    /// <summary>Gets the hue slider.</summary>
    public RenderRect HueBar { get; }

    /// <summary>Gets the swatch of the chosen color.</summary>
    public RenderRect Preview { get; }

    /// <summary>Gets the line showing the chosen color's values.</summary>
    public RenderRect ValueLine { get; }

    /// <summary>Gets the swatches to choose from.</summary>
    public IReadOnlyList<RenderColor> Swatches => _swatches;

    /// <summary>Gets where the swatches are.</summary>
    public IReadOnlyList<RenderRect> SwatchBounds => _swatchRects;

    /// <summary>Gets the chosen color.</summary>
    public RenderColor Color => FromHsv(_hue, _saturation, _brightness);

    /// <inheritdoc />
    public override InputPickerResult PointerDown(double x, double y)
    {
        if (!IsOpen)
        {
            return InputPickerResult.None;
        }

        if (Hit(Area, x, y))
        {
            _drag = DragTarget.Area;
            return Drag(x, y);
        }

        if (Hit(Inflate(HueBar, 4f), x, y))
        {
            _drag = DragTarget.Hue;
            return Drag(x, y);
        }

        for (var index = 0; index < _swatchRects.Count; index++)
        {
            if (Hit(_swatchRects[index], x, y))
            {
                (_hue, _saturation, _brightness) = ToHsv(_swatches[index]);
                Apply();
                return InputPickerResult.Repaint;
            }
        }

        return InputPickerResult.None;
    }

    /// <inheritdoc />
    public override InputPickerResult PointerMove(double x, double y) =>
        IsOpen && _drag != DragTarget.None ? Drag(x, y) : InputPickerResult.None;

    /// <inheritdoc />
    public override InputPickerResult PointerUp(double x, double y)
    {
        var result = PointerMove(x, y);
        _drag = DragTarget.None;
        return result;
    }

    /// <inheritdoc />
    public override InputPickerResult KeyDown(string key)
    {
        if (!IsOpen)
        {
            return InputPickerResult.None;
        }

        switch (key)
        {
            case "ArrowLeft":
                _saturation = Math.Clamp(_saturation - KeyStep, 0f, 1f);
                break;
            case "ArrowRight":
                _saturation = Math.Clamp(_saturation + KeyStep, 0f, 1f);
                break;
            case "ArrowUp":
                _brightness = Math.Clamp(_brightness + KeyStep, 0f, 1f);
                break;
            case "ArrowDown":
                _brightness = Math.Clamp(_brightness - KeyStep, 0f, 1f);
                break;
            case "PageUp":
                _hue = (_hue + 359f) % 360f;
                break;
            case "PageDown":
                _hue = (_hue + 1f) % 360f;
                break;
            default:
                return base.KeyDown(key);
        }

        Apply();
        return InputPickerResult.Repaint;
    }

    /// <inheritdoc />
    protected override void Paint(DisplayList displayList)
    {
        ArgumentNullException.ThrowIfNull(displayList);
        PaintFrame(displayList);

        // saturation across, brightness down, over the pure hue (gradient angles: 0 is to the right, 90 down)
        var radii = Round(2f);
        displayList.FillRect(Area, FromHsv(_hue, 1f, 1f), radii);
        displayList.FillRect(Area, LinearGradient(0f, new RenderColor(255, 255, 255), new RenderColor(255, 255, 255, 0)), radii);
        displayList.FillRect(Area, LinearGradient(90f, new RenderColor(0, 0, 0, 0), new RenderColor(0, 0, 0)), radii);

        var markerX = Area.X + (_saturation * Area.Width);
        var markerY = Area.Y + ((1f - _brightness) * Area.Height);
        displayList.PushClip(Area, radii);
        PaintRing(displayList, markerX, markerY, 6f);
        displayList.PopClip();

        displayList.FillRect(Preview, Color, Round(PreviewSize / 2f));
        displayList.StrokeRoundedRect(Preview, Palette.Border, 1f, Round(PreviewSize / 2f));

        var hues = Enumerable.Range(0, 7).Select(index => new RenderGradientStop(index / 6f, FromHsv((index * 60f) % 360f, 1f, 1f))).ToList();
        displayList.FillRect(HueBar, new RenderGradientPaint(new RenderGradient(RenderGradientKind.Linear, hues, AngleDegrees: 0f)), Round(HueBarHeight / 2f));
        PaintRing(displayList, HueBar.X + (_hue / 360f * HueBar.Width), HueBar.Y + (HueBarHeight / 2f), (HueBarHeight / 2f) + 2f);

        var color = Color;
        var baseline = GetBaseline(ValueLine);
        DrawText(displayList, FormatSimpleColor(color), ValueLine.X, baseline, Palette.Text);
        var rgb = string.Create(CultureInfo.InvariantCulture, $"RGB {color.R}, {color.G}, {color.B}");
        DrawText(displayList, rgb, ValueLine.X + ValueLine.Width - MeasureText(rgb), baseline, Palette.MutedText);

        for (var index = 0; index < _swatchRects.Count; index++)
        {
            var rect = _swatchRects[index];
            displayList.FillRect(rect, _swatches[index], Round(3f));
            var isChosen = _swatches[index] == color;
            displayList.StrokeRoundedRect(isChosen ? Inflate(rect, 2f) : rect, isChosen ? Palette.Accent : Palette.Border, isChosen ? 2f : 1f, Round(isChosen ? 5f : 3f));
        }
    }

    /// <summary>
    /// Gets a valid simple color (`#rrggbb`, HTML's only value of a color input).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="color">The color.</param>
    /// <returns>True if it is a valid simple color.</returns>
    public static bool TryParseSimpleColor(string? value, out RenderColor color)
    {
        color = RenderColor.Black;
        var trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length != 7 || trimmed[0] != '#' ||
            !int.TryParse(trimmed.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new RenderColor((byte)(rgb >> 16), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return true;
    }

    /// <summary>
    /// Formats a color as a simple color.
    /// </summary>
    /// <param name="color">The color, whose alpha is ignored.</param>
    /// <returns>The value, e.g. `#ff8000`.</returns>
    public static string FormatSimpleColor(RenderColor color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}");

    private InputPickerResult Drag(double x, double y)
    {
        if (_drag == DragTarget.Area)
        {
            _saturation = (float)Math.Clamp((x - Area.X) / Area.Width, 0d, 1d);
            _brightness = 1f - (float)Math.Clamp((y - Area.Y) / Area.Height, 0d, 1d);
        }
        else
        {
            _hue = (float)Math.Clamp((x - HueBar.X) / HueBar.Width, 0d, 1d) * 360f;
        }

        Apply();
        return InputPickerResult.Repaint;
    }

    private void Apply() => SetValue(FormatSimpleColor(Color));

    private static void PaintRing(DisplayList displayList, float centerX, float centerY, float radius)
    {
        var outer = new RenderRect(centerX - radius, centerY - radius, 2f * radius, 2f * radius);
        displayList.StrokeRoundedRect(outer, RenderColor.White, 2f, Round(radius));
        displayList.StrokeRoundedRect(Inflate(outer, 1f), new RenderColor(0, 0, 0, 102), 1f, Round(radius + 1f));
    }

    private static RenderRect Inflate(RenderRect rect, float by) =>
        new(rect.X - by, rect.Y - by, rect.Width + (2f * by), rect.Height + (2f * by));

    private static RenderGradientPaint LinearGradient(float angle, RenderColor from, RenderColor to) =>
        new(new RenderGradient(RenderGradientKind.Linear, [new RenderGradientStop(0f, from), new RenderGradientStop(1f, to)], AngleDegrees: angle));

    private static IReadOnlyList<RenderColor> ResolveSwatches(IElement input)
    {
        var list = input.GetAttribute("list") is { Length: > 0 } id ? input.Owner?.GetElementById(id) : null;

        if (list is not null && string.Equals(list.LocalName, "datalist", StringComparison.OrdinalIgnoreCase))
        {
            var suggestions = new List<RenderColor>();

            foreach (var option in list.QuerySelectorAll("option"))
            {
                if (TryParseSimpleColor(option.GetAttribute("value"), out var color) && !suggestions.Contains(color))
                {
                    suggestions.Add(color);
                }
            }

            if (suggestions.Count > 0)
            {
                return suggestions;
            }
        }

        return DefaultSwatches;
    }

    private static (float Hue, float Saturation, float Brightness) ToHsv(RenderColor color)
    {
        var r = color.R / 255f;
        var g = color.G / 255f;
        var b = color.B / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0f ? 0f
            : max == r ? 60f * (((g - b) / delta) % 6f)
            : max == g ? 60f * (((b - r) / delta) + 2f)
            : 60f * (((r - g) / delta) + 4f);
        return (hue < 0f ? hue + 360f : hue, max == 0f ? 0f : delta / max, max);
    }

    private static RenderColor FromHsv(float hue, float saturation, float brightness)
    {
        var chroma = brightness * saturation;
        var section = (hue % 360f) / 60f;
        var x = chroma * (1f - Math.Abs((section % 2f) - 1f));
        var (r, g, b) = (int)section switch
        {
            0 => (chroma, x, 0f),
            1 => (x, chroma, 0f),
            2 => (0f, chroma, x),
            3 => (0f, x, chroma),
            4 => (x, 0f, chroma),
            _ => (chroma, 0f, x),
        };
        var m = brightness - chroma;
        return new RenderColor(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
