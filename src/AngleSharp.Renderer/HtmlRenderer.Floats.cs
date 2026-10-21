namespace AngleSharp.Renderer;

/// <summary>
/// Floats (<c>float: left|right</c>) and <c>clear</c>.
/// </summary>
/// <remarks>
/// Every block formatting context owns one <see cref="FloatArea"/>, holding the margin boxes of
/// the floats placed in it so far, in absolute coordinates. Ordinary blocks share their parent's
/// area, so the line boxes of every later block in the same context wrap around a float, line by
/// line; boxes that establish a new block formatting context get a fresh area and, as a whole,
/// avoid the floats of the outer one.
/// </remarks>
public sealed partial class HtmlRenderer
{
    private sealed class FloatArea
    {
        private readonly List<PlacedFloat> _floats = [];

        private readonly record struct PlacedFloat(float Left, float Top, float Right, float Bottom, bool IsLeft);

        /// <summary>
        /// The free horizontal band within <paramref name="containerLeft"/>..<paramref name="containerRight"/>
        /// for content spanning <paramref name="top"/>..<paramref name="top"/>+<paramref name="height"/>.
        /// </summary>
        public (float Left, float Right) GetBand(float top, float height, float containerLeft, float containerRight)
        {
            var left = containerLeft;
            var right = containerRight;
            var bottom = top + Math.Max(height, 0.01f);

            foreach (var placed in _floats)
            {
                if (placed.Top >= bottom || placed.Bottom <= top)
                {
                    continue;
                }

                if (placed.IsLeft)
                {
                    left = Math.Max(left, placed.Right);
                }
                else
                {
                    right = Math.Min(right, placed.Left);
                }
            }

            return (left, Math.Max(left, right));
        }

        /// <summary>
        /// Whether any float reaches below <paramref name="y"/> - i.e. whether content starting
        /// there may have to flow around one.
        /// </summary>
        public bool HasFloatsBelow(float y)
        {
            foreach (var placed in _floats)
            {
                if (placed.Bottom > y)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The nearest float bottom below <paramref name="y"/>, where the free band can widen next,
        /// or <see cref="float.NaN"/> when no float ends below it.
        /// </summary>
        public float NextBottomBelow(float y)
        {
            var next = float.NaN;

            foreach (var placed in _floats)
            {
                if (placed.Bottom > y && (float.IsNaN(next) || placed.Bottom < next))
                {
                    next = placed.Bottom;
                }
            }

            return next;
        }

        /// <summary>
        /// The position a box with <c>clear: <paramref name="clear"/></c> has to start at, or
        /// <see cref="float.NaN"/> when it clears nothing.
        /// </summary>
        public float GetClearance(string clear)
        {
            var clearsLeft = clear is "left" or "both";
            var clearsRight = clear is "right" or "both";
            var clearance = float.NaN;

            foreach (var placed in _floats)
            {
                if ((placed.IsLeft && clearsLeft) || (!placed.IsLeft && clearsRight))
                {
                    clearance = float.IsNaN(clearance) ? placed.Bottom : Math.Max(clearance, placed.Bottom);
                }
            }

            return clearance;
        }

        /// <summary>
        /// The bottom of the lowest float, or <see cref="float.NaN"/> without any.
        /// </summary>
        public float Bottom
        {
            get
            {
                var bottom = float.NaN;

                foreach (var placed in _floats)
                {
                    bottom = float.IsNaN(bottom) ? placed.Bottom : Math.Max(bottom, placed.Bottom);
                }

                return bottom;
            }
        }

        /// <summary>
        /// Finds where a float's margin box of <paramref name="width"/> goes: at the first
        /// position at or below <paramref name="y"/> - and never above an earlier float - where
        /// it fits next to the floats already placed (or, if it fits nowhere, where nothing else
        /// is beside it).
        /// </summary>
        public (float X, float Y) Place(float width, float y, bool isLeft, float containerLeft, float containerRight)
        {
            foreach (var placed in _floats)
            {
                y = Math.Max(y, placed.Top);
            }

            while (true)
            {
                var (left, right) = GetBand(y, 0.01f, containerLeft, containerRight);
                var fits = right - left >= width - 0.01f;
                var next = NextBottomBelow(y);

                if (fits || float.IsNaN(next))
                {
                    return (isLeft ? left : right - width, y);
                }

                y = next;
            }
        }

        public void Add(float left, float top, float right, float bottom, bool isLeft) =>
            _floats.Add(new PlacedFloat(left, top, right, bottom, isLeft));
    }

    /// <summary>
    /// Whether a box lays out its contents in a block formatting context of its own, which keeps
    /// its floats inside and keeps the outer floats out.
    /// </summary>
    private static bool EstablishesBlockFormattingContext(Dictionary<string, string> styleMap, bool isFloat, bool isOutOfFlow, bool isAtomicInline, bool isFlexItem) =>
        isFloat || isOutOfFlow || isAtomicInline || isFlexItem ||
        ShouldClipOverflow(styleMap) ||
        GetDisplay(styleMap)?.Trim().ToLowerInvariant() is "flow-root" or "table-cell" or "table-caption" or "flex" or "grid" or "inline-flex" or "inline-grid" or "table" or "inline-table";
}
