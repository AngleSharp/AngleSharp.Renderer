namespace AngleSharp.Dom;

using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp.Attributes;
using AngleSharp.Dom.Events;
using AngleSharp.Renderer;

/// <summary>
/// Provides the selection of a document.
/// </summary>
[DomExposed("Document")]
public static class DocumentSelectionExtensions
{
    /// <summary>
    /// Gets the selection of the document, which there is one of for its whole life.
    /// </summary>
    [DomName("getSelection")]
    public static ISelection GetSelection(this IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Selection.For(document);
    }
}

/// <summary>
/// Provides the selection of the document of a window.
/// </summary>
[DomExposed("Window")]
public static class WindowSelectionExtensions
{
    /// <summary>
    /// Gets the selection of the document of the window.
    /// </summary>
    [DomName("getSelection")]
    public static ISelection? GetSelection(this IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Document is { } document ? Selection.For(document) : null;
    }
}

internal sealed class Selection : ISelection
{
    private static readonly ConditionalWeakTable<IDocument, Selection> s_selections = new();
    private readonly IDocument _document;
    private IRange? _range;
    // a drag changes the selection all the time, and a document keeps track of its ranges
    private IRange? _ownRange;
    private bool _isBackward;

    private Selection(IDocument document) => _document = document;

    public static Selection For(IDocument document) => s_selections.GetValue(document, static document => new Selection(document));

    public static Selection? TryGet(IDocument document) => s_selections.TryGetValue(document, out var selection) ? selection : null;

    /// <summary>Gets the range, unless what it is in has left the document.</summary>
    public IRange? Range
    {
        get
        {
            // ranges follow what happens to their nodes, except for a range made without the document
            if (_range is { } range && (!IsInDocument(range.Head) || !IsInDocument(range.Tail)))
            {
                _range = null;
            }

            return _range;
        }
    }

    public INode? AnchorNode => Range is { } range ? _isBackward ? range.Tail : range.Head : null;

    public int AnchorOffset => Range is { } range ? _isBackward ? range.End : range.Start : 0;

    public INode? FocusNode => Range is { } range ? _isBackward ? range.Head : range.Tail : null;

    public int FocusOffset => Range is { } range ? _isBackward ? range.Start : range.End : 0;

    public bool IsCollapsed => Range is not { IsCollapsed: false };

    public int RangeCount => Range is null ? 0 : 1;

    public string Type => Range switch
    {
        null => "None",
        { IsCollapsed: true } => "Caret",
        _ => "Range",
    };

    public string Direction => Range switch
    {
        null or { IsCollapsed: true } => "none",
        _ => _isBackward ? "backward" : "forward",
    };

    public IRange GetRangeAt(int index) =>
        index == 0 && Range is { } range ? range : throw new DomException(DomError.IndexSizeError);

    public void AddRange(IRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (Range is null && IsInDocument(range.Head))
        {
            _range = range;
            _isBackward = false;
            OnChanged();
        }
    }

    public void RemoveRange(IRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (!ReferenceEquals(Range, range))
        {
            throw new DomException(DomError.NotFound);
        }

        RemoveAllRanges();
    }

    public void RemoveAllRanges()
    {
        if (Range is not null)
        {
            _range = null;
            OnChanged();
        }
    }

    public void Collapse(INode? node, int offset = 0)
    {
        if (node is null)
        {
            RemoveAllRanges();
            return;
        }

        SetBaseAndExtent(node, offset, node, offset);
    }

    public void CollapseToStart()
    {
        var range = Range ?? throw new DomException(DomError.InvalidState);
        Collapse(range.Head, range.Start);
    }

    public void CollapseToEnd()
    {
        var range = Range ?? throw new DomException(DomError.InvalidState);
        Collapse(range.Tail, range.End);
    }

    public void Extend(INode node, int offset = 0)
    {
        if (AnchorNode is not { } anchor)
        {
            throw new DomException(DomError.InvalidState);
        }

        SetBaseAndExtent(anchor, AnchorOffset, node, offset);
    }

    public void SetBaseAndExtent(INode anchorNode, int anchorOffset, INode focusNode, int focusOffset)
    {
        ArgumentNullException.ThrowIfNull(anchorNode);
        ArgumentNullException.ThrowIfNull(focusNode);

        if (anchorOffset < 0 || anchorOffset > GetLength(anchorNode) || focusOffset < 0 || focusOffset > GetLength(focusNode))
        {
            throw new DomException(DomError.IndexSizeError);
        }

        // what is not in the document cannot be selected
        if (!IsInDocument(anchorNode) || !IsInDocument(focusNode))
        {
            return;
        }

        if (AnchorNode == anchorNode && AnchorOffset == anchorOffset && FocusNode == focusNode && FocusOffset == focusOffset)
        {
            return;
        }

        var isBackward = Compare(anchorNode, anchorOffset, focusNode, focusOffset) > 0;
        var range = _ownRange ??= _document.CreateRange();
        // the end goes first, for the start not to push it along
        range.EndWith(isBackward ? anchorNode : focusNode, isBackward ? anchorOffset : focusOffset);
        range.StartWith(isBackward ? focusNode : anchorNode, isBackward ? focusOffset : anchorOffset);
        _range = range;
        _isBackward = isBackward;
        OnChanged();
    }

    public void SelectAllChildren(INode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        SetBaseAndExtent(node, 0, node, node.ChildNodes.Length);
    }

    public void DeleteFromDocument() => Range?.ClearContent();

    public bool ContainsNode(INode node, bool allowPartialContainment = false)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (Range is not { } range || node.Parent is not { } parent)
        {
            return false;
        }

        var index = parent.ChildNodes.Index(node);
        var startsBeforeEnd = Compare(parent, index, range.Tail, range.End) < 0;
        var endsAfterStart = Compare(parent, index + 1, range.Head, range.Start) > 0;

        return allowPartialContainment
            ? startsBeforeEnd && endsAfterStart
            : Compare(parent, index, range.Head, range.Start) >= 0 && Compare(parent, index + 1, range.Tail, range.End) <= 0;
    }

    public override string ToString()
    {
        if (Range is not { IsCollapsed: false } range)
        {
            return string.Empty;
        }

        return _document.Context.TryGetDomHarness(out var harness) && harness is InteractiveHtmlRendererState { IsTextSelectionEnabled: true } state
            ? state.GetSelectedText(_document, range)
            : GetSourceText(range);
    }

    // without a layout there is only what the nodes say
    private static string GetSourceText(IRange range)
    {
        var text = new StringBuilder();
        var nodes = new Stack<INode>();
        nodes.Push(range.CommonAncestor);

        while (nodes.Count > 0)
        {
            var node = nodes.Pop();

            if (node is IText { Data: var data })
            {
                var start = ReferenceEquals(node, range.Head) ? range.Start : 0;
                var end = ReferenceEquals(node, range.Tail) ? range.End : data.Length;

                if (Compare(node, data.Length, range.Head, range.Start) > 0 && Compare(node, 0, range.Tail, range.End) < 0 && end > start)
                {
                    text.Append(data, start, end - start);
                }
            }

            for (var index = node.ChildNodes.Length - 1; index >= 0; index--)
            {
                nodes.Push(node.ChildNodes[index]);
            }
        }

        return text.ToString();
    }

    private void OnChanged()
    {
        if (_document.Context.TryGetDomHarness(out var harness) && harness is InteractiveHtmlRendererState state)
        {
            state.OnSelectionChanged();
        }

        _document.Dispatch(new Event("selectionchange"));
    }

    private bool IsInDocument(INode node) => ReferenceEquals(node, _document) || ReferenceEquals(node.Owner, _document) && _document.Contains(node);

    private static int GetLength(INode node) => node is ICharacterData { Data: var data } ? data.Length : node.ChildNodes.Length;

    // the position of a boundary point relative to another (https://dom.spec.whatwg.org/#concept-range-bp-position)
    private static int Compare(INode firstNode, int firstOffset, INode secondNode, int secondOffset)
    {
        if (ReferenceEquals(firstNode, secondNode))
        {
            return firstOffset.CompareTo(secondOffset);
        }

        var position = firstNode.CompareDocumentPosition(secondNode);

        if ((position & DocumentPositions.ContainedBy) != 0)
        {
            // the second is below the first: where does its ancestor there stand
            return ChildIndexOfAncestor(firstNode, secondNode) < firstOffset ? 1 : -1;
        }

        if ((position & DocumentPositions.Contains) != 0)
        {
            return ChildIndexOfAncestor(secondNode, firstNode) < secondOffset ? -1 : 1;
        }

        return (position & DocumentPositions.Following) != 0 ? -1 : 1;
    }

    private static int ChildIndexOfAncestor(INode parent, INode descendant)
    {
        var child = descendant;

        while (child.Parent is { } next && !ReferenceEquals(next, parent))
        {
            child = next;
        }

        return parent.ChildNodes.Index(child);
    }
}
