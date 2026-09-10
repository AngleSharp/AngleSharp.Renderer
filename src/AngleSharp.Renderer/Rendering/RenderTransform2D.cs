namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// A resolved 2D affine transform, in the exact same <c>a, b, c, d, e, f</c> order CSS's own
/// <c>matrix(a, b, c, d, e, f)</c> function uses: a point <c>(x, y)</c> maps to
/// <c>(a*x + c*y + e, b*x + d*y + f)</c>. Backend-agnostic, like every other resolved render value
/// in this namespace - <c>SkiaRenderBackend</c> converts it to an <c>SKMatrix</c> only at paint
/// time. Always fully resolved by the time it reaches <see cref="DisplayList"/> (unlike a
/// gradient's or background-image's center/size, which stay unresolved fractions until paint time
/// because they depend on the image's own natural size): a `transform`'s own geometry - the
/// element's border-box width/height for percentage `translate()` values and for the default
/// `transform-origin` - is already fully known by the time `LayoutElement` builds this value, so
/// there is nothing left to defer.
///
/// Deliberately does not know how to build itself from an individual CSS transform function
/// (`rotate()`, `scale()`, `skew()`, ...) - that parsing and matrix math belongs to AngleSharp.Css
/// (`AngleSharp.Css.Parser.TransformParser`/`ICssTransformFunctionValue.ComputeMatrix`), not to
/// this renderer; `HtmlRenderer.ParseCssTransform`/`TryConvertToRenderTransform` convert
/// AngleSharp.Css's own already-computed `TransformMatrix` into this type, rather than this type
/// re-deriving the same trigonometry AngleSharp.Css already provides. Only <see cref="Translate"/>
/// and <see cref="Multiply"/> remain here, because both are needed regardless of where a matrix's
/// own values came from: applying `transform-origin` (translate to the pivot, apply, translate
/// back) and composing multiple already-resolved function matrices together are generic affine-
/// matrix operations, not CSS-transform-specific parsing or semantics.
/// </summary>
public readonly record struct RenderTransform2D(float A, float B, float C, float D, float E, float F)
{
    /// <summary>The identity transform - painting under it looks exactly as if it were not applied.</summary>
    public static readonly RenderTransform2D Identity = new(1f, 0f, 0f, 1f, 0f, 0f);

    /// <summary>Whether this is the identity transform, i.e. a no-op that can safely be skipped.</summary>
    public bool IsIdentity => this == Identity;

    /// <summary>A translation by <paramref name="x"/>/<paramref name="y"/> pixels.</summary>
    public static RenderTransform2D Translate(float x, float y) => new(1f, 0f, 0f, 1f, x, y);

    /// <summary>
    /// Composes two transforms the way CSS's own multi-function `transform` list does: applying
    /// <paramref name="inner"/> to a point first, then <paramref name="outer"/> to the result -
    /// i.e. the standard matrix product <c>outer * inner</c> in column-vector convention. Building
    /// up a function list left to right as <c>M = Multiply(M, next)</c> for each successive
    /// function exactly matches the CSS Transforms spec's own "post-multiply by each function's
    /// matrix, in the order listed" composition rule.
    /// </summary>
    public static RenderTransform2D Multiply(RenderTransform2D outer, RenderTransform2D inner) => new(
        A: (outer.A * inner.A) + (outer.C * inner.B),
        B: (outer.B * inner.A) + (outer.D * inner.B),
        C: (outer.A * inner.C) + (outer.C * inner.D),
        D: (outer.B * inner.C) + (outer.D * inner.D),
        E: (outer.A * inner.E) + (outer.C * inner.F) + outer.E,
        F: (outer.B * inner.E) + (outer.D * inner.F) + outer.F);
}
