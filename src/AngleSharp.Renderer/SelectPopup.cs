namespace AngleSharp.Renderer;

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using System.Linq;

/// <summary>
/// The list a dropdown (a select showing one option) opens: where it goes in the viewport, what it
/// shows there and which row is where. It isn't part of the layout, a host shows it on top of a
/// frame (see <see cref="RenderedFrame.WithOverlay"/>), so it isn't clipped by what is around the
/// select and changing it needs no layout.
/// </summary>
public sealed class SelectPopup
{
    private const int MaximumVisibleRows = 20;
    private const float BorderWidth = 1f;
    private const float RowPadding = 4f;
    private const float RowExtraHeight = 3f;

    private readonly IReadOnlyList<IElement> _rows;

    private SelectPopup(IReadOnlyList<IElement> rows, double left, double top, double width, double height, double rowHeight, double scrollTop, double maximumScrollTop, DisplayList overlay)
    {
        _rows = rows;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        RowHeight = rowHeight;
        ScrollTop = scrollTop;
        MaximumScrollTop = maximumScrollTop;
        Overlay = overlay;
    }

    /// <summary>Gets the left edge in the viewport.</summary>
    public double Left { get; }

    /// <summary>Gets the top edge in the viewport.</summary>
    public double Top { get; }

    /// <summary>Gets the width, its border included.</summary>
    public double Width { get; }

    /// <summary>Gets the height, its border included.</summary>
    public double Height { get; }

    /// <summary>Gets the height of a row.</summary>
    public double RowHeight { get; }

    /// <summary>Gets how far the rows are scrolled.</summary>
    public double ScrollTop { get; }

    /// <summary>Gets how far the rows can be scrolled.</summary>
    public double MaximumScrollTop { get; }

    /// <summary>Gets what to show on top of a frame, in viewport coordinates.</summary>
    public DisplayList Overlay { get; }

    /// <summary>
    /// Gets if the position in the viewport is on the popup.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>True if the position is on the popup.</returns>
    public bool Contains(double x, double y) =>
        x >= Left && x < Left + Width && y >= Top && y < Top + Height;

    /// <summary>
    /// Gets the option or option group shown at the given position in the viewport.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>The element of the row, or null if there is no row at that position.</returns>
    public IElement? GetRowAt(double x, double y)
    {
        if (!Contains(x, y))
        {
            return null;
        }

        var index = (int)Math.Floor((y - Top - BorderWidth + ScrollTop) / RowHeight);
        return index >= 0 && index < _rows.Count ? _rows[index] : null;
    }

    /// <summary>
    /// Gets the scroll position closest to the current one that shows the given row.
    /// </summary>
    /// <param name="row">The option or option group.</param>
    /// <returns>The scroll position.</returns>
    public double GetScrollTopShowing(IElement row)
    {
        var index = _rows.ToList().IndexOf(row);

        if (index < 0)
        {
            return ScrollTop;
        }

        var top = index * RowHeight;
        var visible = Height - (2 * BorderWidth);
        return top < ScrollTop ? top : top + RowHeight > ScrollTop + visible ? top + RowHeight - visible : ScrollTop;
    }

    /// <summary>
    /// Creates the popup of a dropdown as it currently is in the viewport.
    /// </summary>
    /// <param name="select">The select element.</param>
    /// <param name="highlighted">The row to highlight, if any.</param>
    /// <param name="scrollTop">How far to scroll the rows, which is clamped.</param>
    /// <returns>The popup, or null if the select is not shown.</returns>
    public static SelectPopup? Create(IElement select, IElement? highlighted, double scrollTop) =>
        Create(select, highlighted, scrollTop, 0d, 0d, null);

    /// <summary>
    /// Creates the popup of a select in an iframe, which opens in the viewport of the page.
    /// </summary>
    /// <param name="select">The select.</param>
    /// <param name="highlighted">The highlighted option, if any.</param>
    /// <param name="scrollTop">How far the rows are scrolled.</param>
    /// <param name="originX">Where the viewport of the document of the select is horizontally.</param>
    /// <param name="originY">Where the viewport of the document of the select is vertically.</param>
    /// <param name="page">The device of the page, if it isn't that of the document of the select.</param>
    /// <returns>The popup, or null if there is nothing to show.</returns>
    public static SelectPopup? Create(IElement select, IElement? highlighted, double scrollTop, double originX, double originY, IRenderDevice? page)
    {
        ArgumentNullException.ThrowIfNull(select);

        if (select.Owner is not { } document ||
            ElementCssomViewExtensions.GetViewportMetrics(select) is not { } metrics ||
            metrics.BorderBoxWidth <= 0f)
        {
            return null;
        }

        var style = select.ComputeCurrentStyle();
        var device = page ?? document.Context.GetDomHarness().RenderDevice;
        var fontSize = DocumentCssomViewExtensions.ParseLengthOrDefault(style?.GetPropertyValue("font-size"), (float)device.FontSize);
        var font = DocumentCssomViewExtensions.CreateFont(style, document, fontSize);
        var measurer = new SkiaTextMeasurer();
        var rowHeight = Math.Max(((IFontMetricsMeasurer)measurer).MeasureLineSpacing(font), fontSize * 1.2f) + RowExtraHeight;
        var rows = HtmlRenderer.GetListBoxRowInfos(select);
        var boldFont = font with { FontWeight = 700f };
        var widest = rows.Select(row => row.Indent + (row.Label.Length == 0 ? 0f : measurer.MeasureWidth(row.Label, row.IsGroup ? boldFont : font))).DefaultIfEmpty(0f).Max();

        var viewportWidth = (float)device.ViewPortWidth;
        var viewportHeight = (float)device.ViewPortHeight;
        var width = Math.Min(viewportWidth, Math.Max(metrics.BorderBoxWidth, widest + (2f * (RowPadding + BorderWidth))));
        var fullHeight = (Math.Min(rows.Count, MaximumVisibleRows) * rowHeight) + (2f * BorderWidth);

        // below the select if it fits, else on the side having more room
        var selectTop = metrics.BorderBoxY + (float)originY;
        var selectBottom = selectTop + metrics.BorderBoxHeight;
        var below = viewportHeight - selectBottom;
        var above = selectTop;
        var opensBelow = fullHeight <= below || below >= above;
        var height = Math.Max(rowHeight + (2f * BorderWidth), Math.Min(fullHeight, opensBelow ? below : above));
        var top = opensBelow ? selectBottom : selectTop - height;
        var left = Math.Clamp(metrics.BorderBoxX + (float)originX, 0f, Math.Max(0f, viewportWidth - width));

        var visibleHeight = height - (2f * BorderWidth);
        var maximum = Math.Max(0f, (rows.Count * rowHeight) - visibleHeight);
        var scroll = Math.Clamp((float)scrollTop, 0f, maximum);

        var palette = Palette.Resolve(select, style, device, document);
        var overlay = new DisplayList { Fonts = font.Faces ?? FontFaceSet.Empty };
        overlay.FillRect(new RenderRect(left, top, width, height), palette.Border);
        var rowsLeft = left + BorderWidth;
        var rowsTop = top + BorderWidth;
        var rowsWidth = width - (2f * BorderWidth);
        overlay.FillRect(new RenderRect(rowsLeft, rowsTop, rowsWidth, visibleHeight), palette.Background);

        if (palette.AuthorBackground is { } authorBackground)
        {
            overlay.FillRect(new RenderRect(rowsLeft, rowsTop, rowsWidth, visibleHeight), authorBackground);
        }

        overlay.PushClip(new RenderRect(rowsLeft, rowsTop, rowsWidth, visibleHeight), default);

        for (var index = 0; index < rows.Count; index++)
        {
            var rowY = rowsTop + (index * rowHeight) - scroll;

            if (rowY + rowHeight <= rowsTop || rowY >= rowsTop + visibleHeight)
            {
                continue;
            }

            var row = rows[index];
            var isHighlighted = ReferenceEquals(row.Element, highlighted);
            var rowStyle = row.Element.ComputeCurrentStyle();

            if (isHighlighted)
            {
                overlay.FillRect(new RenderRect(rowsLeft, rowY, rowsWidth, rowHeight), palette.HighlightBackground);
            }
            else if (HtmlRenderer.ParseColor(rowStyle?.GetPropertyValue("background-color"), Palette.Transparent) is { A: > 0 } rowBackground)
            {
                overlay.FillRect(new RenderRect(rowsLeft, rowY, rowsWidth, rowHeight), rowBackground);
            }

            if (row.Label.Length > 0)
            {
                var color = isHighlighted ? palette.HighlightText
                    : row.Option is not null && HtmlRenderer.IsOptionDisabled(row.Option) ? palette.DisabledText
                    : palette.ResolveRowText(rowStyle);
                var rowFont = row.IsGroup ? boldFont : font;
                overlay.DrawText(row.Label, rowsLeft + RowPadding + row.Indent, HtmlRenderer.ResolveControlTextBaseline(fontSize, rowY, rowHeight), color, fontSize, rowFont.FontFamily, rowFont.FontWeight, rowFont.IsItalic);
            }
        }

        overlay.PopClip();
        return new SelectPopup(rows.Select(row => row.Element).ToList(), left, top, width, height, rowHeight, scroll, maximum, overlay);
    }

    /// <summary>
    /// The colors of a popup: those of the color scheme the select uses, unless its author chose
    /// some. A preference alone doesn't change them, the page has to support the scheme (see
    /// <see cref="UsesDarkColorScheme"/>).
    /// </summary>
    private readonly record struct Palette(RenderColor Background, RenderColor? AuthorBackground, RenderColor Text, RenderColor? SelectText, RenderColor DisabledText, RenderColor Border, RenderColor HighlightBackground, RenderColor HighlightText)
    {
        public static readonly RenderColor Transparent = new(0, 0, 0, 0);

        // what the default style sheet gives a select, which isn't chosen by the author
        private static readonly RenderColor DefaultSelectBackground = new(232, 232, 232);

        public static Palette Resolve(IElement select, ICssStyleDeclaration? style, IRenderDevice device, IDocument document)
        {
            var isDark = UsesDarkColorScheme(style, device, document);
            var background = HtmlRenderer.ParseColor(style?.GetPropertyValue("background-color"), Transparent);
            var text = HtmlRenderer.ParseColor(style?.GetPropertyValue("color"), RenderColor.Black);

            return new Palette(
                isDark ? new RenderColor(59, 59, 59) : RenderColor.White,
                background.A == 0 || background == DefaultSelectBackground ? null : background,
                isDark ? RenderColor.White : RenderColor.Black,
                text == RenderColor.Black ? null : text,
                isDark ? new RenderColor(170, 170, 170) : new RenderColor(109, 109, 109),
                isDark ? new RenderColor(133, 133, 133) : new RenderColor(118, 118, 118),
                isDark ? new RenderColor(153, 200, 255) : new RenderColor(0, 117, 255),
                isDark ? RenderColor.Black : RenderColor.White);
        }

        /// <summary>
        /// The text of a row: what its option has of its own, else the select's, else the scheme's.
        /// </summary>
        public RenderColor ResolveRowText(ICssStyleDeclaration? rowStyle)
        {
            var color = HtmlRenderer.ParseColor(rowStyle?.GetPropertyValue("color"), RenderColor.Black);
            return color == RenderColor.Black || color == SelectText ? SelectText ?? Text : color;
        }
    }

    /// <summary>
    /// If an element uses the dark color scheme (CSS Color Adjustment 1 §2): when it (or else the
    /// page's `color-scheme` meta element) supports it, and also light only if that's not preferred.
    /// </summary>
    private static bool UsesDarkColorScheme(ICssStyleDeclaration? style, IRenderDevice device, IDocument document)
    {
        var value = style?.GetPropertyValue("color-scheme")?.Trim();

        if (string.IsNullOrEmpty(value) || string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase))
        {
            value = document.QuerySelector("meta[name='color-scheme' i]")?.GetAttribute("content");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var schemes = value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return schemes.Contains("dark") &&
            (!schemes.Contains("light") || string.Equals(device.GetPreference("prefers-color-scheme"), "dark", StringComparison.OrdinalIgnoreCase));
    }
}
