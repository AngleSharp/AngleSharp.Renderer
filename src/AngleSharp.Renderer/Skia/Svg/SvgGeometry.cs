namespace AngleSharp.Renderer.Skia.Svg;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Computes the (approximate) geometric bounding box of an element's rendered content, in its own
/// local user space - the "object bounding box" `mask`'s default region and gradient
/// `objectBoundingBox` units are defined against. Descends into `g`/`a`/`svg` and resolved `use`
/// targets; text content is not measured and contributes no bounds, a documented simplification.
/// </summary>
internal static class SvgGeometry
{
    public static SKRect? ComputeBounds(IElement element, SvgViewport viewport, SvgRenderContext context) =>
        ComputeBounds(element, viewport, context, []);

    private static SKRect? ComputeBounds(IElement element, SvgViewport viewport, SvgRenderContext context, HashSet<IElement> visited)
    {
        if (!visited.Add(element))
        {
            return null;
        }

        try
        {
            var localBounds = ComputeLocalBounds(element, viewport, context, visited);

            if (localBounds is not { } bounds)
            {
                return null;
            }

            var transform = SvgTransformParser.Parse(element.GetAttribute("transform"));
            return transform.IsIdentity ? bounds : transform.MapRect(bounds);
        }
        finally
        {
            visited.Remove(element);
        }
    }

    private static SKRect? ComputeLocalBounds(IElement element, SvgViewport viewport, SvgRenderContext context, HashSet<IElement> visited)
    {
        var tag = element.LocalName.ToLowerInvariant();

        switch (tag)
        {
            case "g":
            case "a":
            case "svg":
            case "symbol":
                return UnionChildren(element.Children, viewport, context, visited);

            case "use":
            {
                var href = SvgUrlReference.GetHref(element);

                if (href is not { Length: > 1 } || !href.StartsWith('#') || !context.ElementsById.TryGetValue(href[1..], out var referenced))
                {
                    return null;
                }

                var x = SvgShapeBuilder.ReadX(element, "x", viewport);
                var y = SvgShapeBuilder.ReadY(element, "y", viewport);
                var referencedBounds = ComputeBounds(referenced, viewport, context, visited);

                return referencedBounds is { } rb ? SKRect.Create(rb.Left + x, rb.Top + y, rb.Width, rb.Height) : null;
            }

            default:
                using (var path = SvgShapeBuilder.Build(element, viewport))
                {
                    return path is { IsEmpty: false } ? path.Bounds : null;
                }
        }
    }

    private static SKRect? UnionChildren(IEnumerable<IElement> children, SvgViewport viewport, SvgRenderContext context, HashSet<IElement> visited)
    {
        SKRect? union = null;

        foreach (var child in children)
        {
            var childBounds = ComputeBounds(child, viewport, context, visited);

            if (childBounds is not { } bounds)
            {
                continue;
            }

            union = union is { } existing ? SKRect.Union(existing, bounds) : bounds;
        }

        return union;
    }
}
