namespace AngleSharp.Renderer;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Css.Painting;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// The `paint()` images of the CSS Painting API: a paint worklet (registered with
/// AngleSharp.Css's <see cref="PaintWorklet"/>) draws them for the size of the box when it is
/// painted, recording what it draws, which is replayed on a canvas here.
/// </summary>
public partial class HtmlRenderer
{
    /// <summary>
    /// A `paint()` layer of a background, drawn once the size of the box is known.
    /// </summary>
    private sealed record PaintWorkletLayer(int Index) : RenderPaint;

    /// <summary>
    /// The box whose decorations are painted, which the paint worklets get the style of.
    /// </summary>
    [ThreadStatic]
    private static ElementRenderNode? s_paintedNode;

    // a document of its own for the canvases the images are drawn on, which no page shows
    [ThreadStatic]
    private static IDocument? s_paintDocument;

    /// <summary>
    /// Draws the paint() image of the layer (the given one of the property's paint() images) for
    /// an area of the given size, or returns null if there is none (e.g. no worklet registered
    /// the name yet, or it failed).
    /// </summary>
    private static RenderImagePaint? ResolvePaintWorkletLayer(PaintWorkletLayer layer, string property, float width, float height)
    {
        if (s_paintedNode is not { } node || width <= 0f || height <= 0f)
        {
            return null;
        }

        IReadOnlyList<PaintImage?> images;

        try
        {
            images = node.DrawPaintImages(property, new PaintSize(width, height));
        }
        catch (Exception)
        {
            return null;
        }

        if (layer.Index >= images.Count || images[layer.Index] is not { Context: RecordingPaintRenderingContext2D recording } image)
        {
            return null;
        }

        var rendered = RasterizePaintImage(recording.Commands, width, height, image.DevicePixelRatio);
        return rendered is null
            ? null
            : new RenderImagePaint(rendered, false, false, RenderBackgroundPositionComponent.Zero, RenderBackgroundPositionComponent.Zero, RenderBackgroundSize.Auto);
    }

    private static RenderedImage? RasterizePaintImage(IReadOnlyList<PaintCommand> commands, float width, float height, double ratio)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * ratio));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * ratio));
        var document = s_paintDocument ??= new HtmlParser().ParseDocument(string.Empty);

        if (document.CreateElement(TagNames.Canvas) is not IHtmlCanvasElement canvas)
        {
            return null;
        }

        canvas.Width = pixelWidth;
        canvas.Height = pixelHeight;
        var context = new Canvas2DRenderingContext(canvas);
        context.Scale(ratio, ratio);

        foreach (var command in commands)
        {
            try
            {
                Replay(context, command);
            }
            catch (Exception)
            {
                // what can't be replayed is left out, as an invalid call of a script would be
            }
        }

        // the image is as large as the area, its pixels as many as the device has there
        return new RenderedImage(context.ToImage("image/png"), (int)Math.Ceiling(width), (int)Math.Ceiling(height), "image/png");
    }

    private static void Replay(Canvas2DRenderingContext context, PaintCommand command)
    {
        var args = command.Arguments;
        double D(int index) => Convert.ToDouble(args[index], CultureInfo.InvariantCulture);
        string S(int index) => Convert.ToString(args[index], CultureInfo.InvariantCulture) ?? string.Empty;
        string Rule(int index) => args.Count > index && args[index] is CanvasFillRule rule && rule == CanvasFillRule.EvenOdd ? "evenodd" : "nonzero";

        switch (command.Name)
        {
            case "save": context.Save(); break;
            case "restore": context.Restore(); break;
            case "reset": context.Reset(); break;
            case "scale": context.Scale(D(0), D(1)); break;
            case "rotate": context.Rotate(D(0)); break;
            case "translate": context.Translate(D(0), D(1)); break;
            case "transform": context.Transform(D(0), D(1), D(2), D(3), D(4), D(5)); break;
            case "setTransform": context.SetTransform(D(0), D(1), D(2), D(3), D(4), D(5)); break;
            case "resetTransform": context.ResetTransform(); break;
            case "globalAlpha": context.GlobalAlpha = D(0); break;
            case "globalCompositeOperation": context.GlobalCompositeOperation = S(0); break;
            case "imageSmoothingEnabled": context.ImageSmoothingEnabled = Convert.ToBoolean(args[0], CultureInfo.InvariantCulture); break;
            case "fillStyle": context.FillStyle = ToCanvasStyle(context, args[0]); break;
            case "strokeStyle": context.StrokeStyle = ToCanvasStyle(context, args[0]); break;
            case "shadowOffsetX": context.ShadowOffsetX = D(0); break;
            case "shadowOffsetY": context.ShadowOffsetY = D(0); break;
            case "shadowBlur": context.ShadowBlur = D(0); break;
            case "shadowColor": context.ShadowColor = S(0); break;
            case "clearRect": context.ClearRect(D(0), D(1), D(2), D(3)); break;
            case "fillRect": context.FillRect(D(0), D(1), D(2), D(3)); break;
            case "strokeRect": context.StrokeRect(D(0), D(1), D(2), D(3)); break;
            case "beginPath": context.BeginPath(); break;
            case "fill" when args.Count <= 1: context.Fill(Rule(0)); break;
            case "stroke" when args.Count == 0: context.Stroke(); break;
            case "clip" when args.Count <= 1: context.Clip(Rule(0)); break;
            case "lineWidth": context.LineWidth = D(0); break;
            case "lineCap": context.LineCap = S(0); break;
            case "lineJoin": context.LineJoin = S(0); break;
            case "miterLimit": context.MiterLimit = D(0); break;
            case "setLineDash": context.SetLineDash(args[0] as IEnumerable<double>); break;
            case "lineDashOffset": context.LineDashOffset = D(0); break;
            case "closePath": context.ClosePath(); break;
            case "moveTo": context.MoveTo(D(0), D(1)); break;
            case "lineTo": context.LineTo(D(0), D(1)); break;
            case "quadraticCurveTo": context.QuadraticCurveTo(D(0), D(1), D(2), D(3)); break;
            case "bezierCurveTo": context.BezierCurveTo(D(0), D(1), D(2), D(3), D(4), D(5)); break;
            case "arcTo": context.ArcTo(D(0), D(1), D(2), D(3), D(4)); break;
            case "rect": context.Rect(D(0), D(1), D(2), D(3)); break;
            case "roundRect": context.RoundRect(D(0), D(1), D(2), D(3), args[4] is double[] { Length: > 0 } radii ? radii[0] : 0d); break;
            case "arc": context.Arc(D(0), D(1), D(2), D(3), D(4), Convert.ToBoolean(args[5], CultureInfo.InvariantCulture)); break;
            case "ellipse": context.Ellipse(D(0), D(1), D(2), D(3), D(4), D(5), D(6), Convert.ToBoolean(args[7], CultureInfo.InvariantCulture)); break;
        }
    }

    private static object ToCanvasStyle(Canvas2DRenderingContext context, object style)
    {
        if (style is not AngleSharp.Css.Painting.CanvasGradient gradient)
        {
            return Convert.ToString(style, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var p = gradient.Parameters;
        var result = gradient.Kind switch
        {
            CanvasGradientKind.Radial => context.CreateRadialGradient(p[0], p[1], p[2], p[3], p[4], p[5]),
            CanvasGradientKind.Conic => context.CreateConicGradient(p[0], p[1], p[2]),
            _ => context.CreateLinearGradient(p[0], p[1], p[2], p[3]),
        };

        foreach (var stop in gradient.ColorStops)
        {
            result.AddColorStop(stop.Offset, stop.Color);
        }

        return result;
    }
}
