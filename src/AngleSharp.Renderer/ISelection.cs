namespace AngleSharp.Dom;

using AngleSharp.Attributes;

/// <summary>
/// The selection of a document, see https://w3c.github.io/selection-api/.
/// </summary>
[DomName("Selection")]
[DomExposed("Window")]
public interface ISelection
{
    /// <summary>Gets the node the selection starts from, which may be after its focus.</summary>
    [DomName("anchorNode")]
    INode? AnchorNode { get; }

    /// <summary>Gets the offset in <see cref="AnchorNode"/>.</summary>
    [DomName("anchorOffset")]
    int AnchorOffset { get; }

    /// <summary>Gets the node the selection extends to.</summary>
    [DomName("focusNode")]
    INode? FocusNode { get; }

    /// <summary>Gets the offset in <see cref="FocusNode"/>.</summary>
    [DomName("focusOffset")]
    int FocusOffset { get; }

    /// <summary>Gets if nothing is selected.</summary>
    [DomName("isCollapsed")]
    bool IsCollapsed { get; }

    /// <summary>Gets the number of ranges: 0 or 1.</summary>
    [DomName("rangeCount")]
    int RangeCount { get; }

    /// <summary>Gets "None", "Caret" or "Range".</summary>
    [DomName("type")]
    string Type { get; }

    /// <summary>Gets "none", "forward" or "backward".</summary>
    [DomName("direction")]
    string Direction { get; }

    /// <summary>Gets the range of the selection.</summary>
    /// <param name="index">The index, only 0 can be there.</param>
    [DomName("getRangeAt")]
    IRange GetRangeAt(int index);

    /// <summary>Makes the range the selection, unless there already is one.</summary>
    [DomName("addRange")]
    void AddRange(IRange range);

    /// <summary>Removes the range, if it is the selection.</summary>
    [DomName("removeRange")]
    void RemoveRange(IRange range);

    /// <summary>Removes the selection.</summary>
    [DomName("removeAllRanges")]
    [DomName("empty")]
    void RemoveAllRanges();

    /// <summary>Puts the caret at the given place.</summary>
    [DomName("collapse")]
    [DomName("setPosition")]
    void Collapse(INode? node, int offset = 0);

    /// <summary>Puts the caret at the start of the selection.</summary>
    [DomName("collapseToStart")]
    void CollapseToStart();

    /// <summary>Puts the caret at the end of the selection.</summary>
    [DomName("collapseToEnd")]
    void CollapseToEnd();

    /// <summary>Moves the focus of the selection, keeping its anchor.</summary>
    [DomName("extend")]
    void Extend(INode node, int offset = 0);

    /// <summary>Sets both ends of the selection.</summary>
    [DomName("setBaseAndExtent")]
    void SetBaseAndExtent(INode anchorNode, int anchorOffset, INode focusNode, int focusOffset);

    /// <summary>Selects the children of the node.</summary>
    [DomName("selectAllChildren")]
    void SelectAllChildren(INode node);

    /// <summary>Removes what is selected from the document.</summary>
    [DomName("deleteFromDocument")]
    void DeleteFromDocument();

    /// <summary>Gets if the node is selected, or only partly with <paramref name="allowPartialContainment"/>.</summary>
    [DomName("containsNode")]
    bool ContainsNode(INode node, bool allowPartialContainment = false);

    /// <summary>Gets the selected text as it is shown.</summary>
    [DomName("toString")]
    string ToString();
}
