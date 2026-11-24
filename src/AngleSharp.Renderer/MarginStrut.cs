namespace AngleSharp.Renderer;

/// <summary>
/// Adjoining vertical margins that have not been placed yet (a "margin strut"): they collapse
/// into the largest positive one plus the most negative one (CSS 2.1 §8.3.1). Keeping both
/// parts, rather than their sum, collapses a chain of margins correctly, e.g. those of an empty
/// block between others (75px and -72px collapse through it to 3px, which joins 48px before it
/// to 3px again, not to 48px).
/// </summary>
internal readonly record struct MarginStrut(float Positive, float Negative)
{
    public static readonly MarginStrut Zero = default;

    /// <summary>
    /// The space the margins take: the largest positive one plus the most negative one.
    /// </summary>
    public float Value => Positive + Negative;

    public static MarginStrut From(float margin) =>
        float.IsNaN(margin) ? Zero : margin >= 0f ? new MarginStrut(margin, 0f) : new MarginStrut(0f, margin);

    /// <summary>
    /// The margins collapsed with another one.
    /// </summary>
    public MarginStrut Append(MarginStrut other) =>
        new(Math.Max(Positive, other.Positive), Math.Min(Negative, other.Negative));

    public MarginStrut Append(float margin) => Append(From(margin));

    public static implicit operator float(MarginStrut strut) => strut.Value;

    public static implicit operator MarginStrut(float margin) => From(margin);
}
