namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// A backend-agnostic CSS `clip-path` basic shape, already fully resolved to absolute pixel
/// coordinates against the element's own border box - mirroring how <see cref="RenderTransform2D"/>
/// and <see cref="RenderFilterFunction"/> carry only already-resolved geometry, with no further
/// percentage/keyword resolution left for the backend to perform.
/// </summary>
public abstract record RenderClipShape;

/// <summary>`circle(&lt;radius&gt; at &lt;position&gt;)`.</summary>
public sealed record RenderClipCircle(float CenterX, float CenterY, float Radius) : RenderClipShape;

/// <summary>`ellipse(&lt;radius-x&gt; &lt;radius-y&gt; at &lt;position&gt;)`.</summary>
public sealed record RenderClipEllipse(float CenterX, float CenterY, float RadiusX, float RadiusY) : RenderClipShape;

/// <summary>`inset(&lt;top&gt; &lt;right&gt; &lt;bottom&gt; &lt;left&gt; round &lt;radius&gt;)`.</summary>
public sealed record RenderClipInset(RenderRect Rect, RenderCornerRadii Radii) : RenderClipShape;

/// <summary>`polygon(&lt;x1&gt; &lt;y1&gt;, &lt;x2&gt; &lt;y2&gt;, ...)`.</summary>
public sealed record RenderClipPolygon(IReadOnlyList<(float X, float Y)> Points) : RenderClipShape;
