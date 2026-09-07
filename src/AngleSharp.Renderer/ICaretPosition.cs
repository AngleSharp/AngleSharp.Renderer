namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Geometry;

/// <summary>
/// Represents a caret position in the document.
/// </summary>
[DomName("CaretPosition")]
[DomExposed("Window")]
public interface ICaretPosition
{
    /// <summary>
    /// Gets the node that contains the caret.
    /// </summary>
    [DomName("offsetNode")]
    INode OffsetNode { get; }

    /// <summary>
    /// Gets the UTF-16 code unit offset within <see cref="OffsetNode"/>.
    /// </summary>
    [DomName("offset")]
    int Offset { get; }

    /// <summary>
    /// Gets the caret client rectangle.
    /// </summary>
    [DomName("getClientRect")]
    IDomRect GetClientRect();
}
