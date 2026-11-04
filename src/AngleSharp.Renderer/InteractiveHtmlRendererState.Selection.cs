namespace AngleSharp.Renderer;

using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Text selection with the pointer. A selection only changes what is painted, so it does not
/// need a layout: the last frame knows where its text is, and shows it (see
/// <see cref="RenderedFrame.WithSelection"/>).
/// </summary>
internal sealed partial class InteractiveHtmlRendererState
{
    private bool _isTextSelectionEnabled = true;
    private SelectionGesture? _gesture;
    // what the last frame shows, for not painting it again
    private (RenderedFrame Base, RenderedFrame Shown, HtmlRenderer.TextSelectionRange? Selection)? _shownSelection;

    private sealed record SelectionGesture(int ClickCount, TextCaret AnchorStart, TextCaret AnchorEnd);

    private readonly record struct TextCaret(IText Node, int Offset);

    public bool IsTextSelectionEnabled
    {
        get => _isTextSelectionEnabled;
        set
        {
            if (_isTextSelectionEnabled != value)
            {
                _isTextSelectionEnabled = value;
                _gesture = null;
                _lastFrame = null;

                if (!value && Context.Active is { } document)
                {
                    Selection.TryGet(document)?.RemoveAllRanges();
                }

                // whether layouts record their text has changed
                OnPaintInvalidated();
            }
        }
    }

    public bool IsSelectingText => _gesture is not null;

    public bool BeginTextSelection(double x, double y, int clickCount = 1, bool extend = false)
    {
        _gesture = null;

        if (!_isTextSelectionEnabled || Context.Active is not { } document)
        {
            return false;
        }

        var selection = Selection.For(document);
        var (map, hit, listY, scrollDelta) = GetTextMapAt(document, x, y);

        if (hit is not null && map.GetUserSelect(hit) == "none" && !extend)
        {
            selection.RemoveAllRanges();
            return false;
        }

        if (map.Locate((float)x, listY, scrollDelta, hit) is not { } located)
        {
            selection.RemoveAllRanges();
            return false;
        }

        if (extend && selection.AnchorNode is IText anchorNode)
        {
            var anchor = new TextCaret(anchorNode, selection.AnchorOffset);
            _gesture = new SelectionGesture(1, anchor, anchor);
            selection.Extend(located.Node, located.Offset);
            return true;
        }

        if (!FireSelectStart(located.Node))
        {
            return false;
        }

        var (start, end) = GetUnit(map, located, clickCount);
        _gesture = new SelectionGesture(clickCount, start, end);
        selection.SetBaseAndExtent(start.Node, start.Offset, end.Node, end.Offset);
        return true;
    }

    public void ExtendTextSelection(double x, double y)
    {
        if (_gesture is not { } gesture || Context.Active is not { } document)
        {
            return;
        }

        var (map, hit, listY, scrollDelta) = GetTextMapAt(document, x, y);

        if (map.Locate((float)x, listY, scrollDelta, hit) is not { } located)
        {
            return;
        }

        var (start, end) = GetUnit(map, located, gesture.ClickCount);
        var selection = Selection.For(document);
        var isBefore = gesture.AnchorStart.Node.CompareDocumentPosition(start.Node) is var position &&
            (ReferenceEquals(gesture.AnchorStart.Node, start.Node) ? start.Offset < gesture.AnchorStart.Offset : (position & DocumentPositions.Preceding) != 0);

        if (isBefore)
        {
            selection.SetBaseAndExtent(gesture.AnchorEnd.Node, gesture.AnchorEnd.Offset, start.Node, start.Offset);
        }
        else
        {
            selection.SetBaseAndExtent(gesture.AnchorStart.Node, gesture.AnchorStart.Offset, end.Node, end.Offset);
        }
    }

    public void EndTextSelection() => _gesture = null;

    public void SelectAllText()
    {
        if (_isTextSelectionEnabled && Context.Active is { } document && (document.Body ?? document.DocumentElement) is { } root)
        {
            Selection.For(document).SelectAllChildren(root);
        }
    }

    public void ClearTextSelection()
    {
        _gesture = null;

        if (Context.Active is { } document)
        {
            Selection.TryGet(document)?.RemoveAllRanges();
        }
    }

    public string SelectedText => Context.Active is { } document && Selection.TryGet(document) is { } selection ? selection.ToString() : string.Empty;

    /// <summary>Gets the selected text as the last frame shows it.</summary>
    internal string GetSelectedText(IDocument document, IRange range)
    {
        var map = GetTextFrame(document).TextMap;
        return map.GetText(map.Resolve(range));
    }

    internal void OnSelectionChanged() => OnPaintInvalidated(keepsMetrics: true);

    private bool FireSelectStart(INode node) =>
        node.Dispatch(new Event("selectstart", bubbles: true, cancelable: true));

    /// <summary>What is selected at once: a character, a word, a paragraph, or what has `user-select: all`.</summary>
    private static (TextCaret Start, TextCaret End) GetUnit(HtmlRenderer.TextMap map, HtmlRenderer.TextCaret located, int clickCount)
    {
        if (map.GetSelectAllRoot(located.Node.ParentElement) is { } root && map.GetExtent(root) is { } rootExtent)
        {
            return (new TextCaret(rootExtent.Start.Node, rootExtent.Start.Offset), new TextCaret(rootExtent.End.Node, rootExtent.End.Offset));
        }

        if (clickCount >= 3 && map.GetBlock(located.Node.ParentElement) is { } block && map.GetExtent(block) is { } extent)
        {
            return (new TextCaret(extent.Start.Node, extent.Start.Offset), new TextCaret(extent.End.Node, extent.End.Offset));
        }

        if (clickCount == 2)
        {
            var (node, offset) = located.Character;
            var (start, end) = GetWord(node.Data, offset);
            return (new TextCaret(node, start), new TextCaret(node, end));
        }

        var caret = new TextCaret(located.Node, located.Offset);
        return (caret, caret);
    }

    // a word, a run of white space, or anything else on its own
    private static (int Start, int End) GetWord(string text, int offset)
    {
        if (offset >= text.Length)
        {
            return (text.Length, text.Length);
        }

        var kind = GetKind(text[offset]);

        if (kind == 2)
        {
            return (offset, offset + (char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length ? 2 : 1));
        }

        var start = offset;
        var end = offset + 1;

        while (start > 0 && GetKind(text[start - 1]) == kind)
        {
            start--;
        }

        while (end < text.Length && GetKind(text[end]) == kind)
        {
            end++;
        }

        return (start, end);

        static int GetKind(char c) => char.IsWhiteSpace(c) ? 1 : char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark ? 0 : 2;
    }

    private (HtmlRenderer.TextMap Map, IElement? Hit, float ListY, float ScrollDelta) GetTextMapAt(IDocument document, double x, double y)
    {
        var frame = GetTextFrame(document);
        var scrollTop = document.DocumentElement is { } root ? GetScrollTop(root, double.MaxValue) : 0d;
        var scrollDelta = (float)(scrollTop - frame.ScrollTop);
        var hit = (scrollDelta == 0f ? frame : frame.Reposition(scrollTop)).HitTestMap.HitTest(x, y)?.Element;

        // what is in an iframe is for the harness of its document
        return (frame.TextMap, ReferenceEquals(hit?.Owner, document) ? hit : null, (float)y + frame.OverscanTop, scrollDelta);
    }

    /// <summary>
    /// Gets a frame with the text as it is now: the last one, unless something changed the
    /// layout since. It may be at another scroll position, as long as it can get there.
    /// </summary>
    private RenderedFrame GetTextFrame(IDocument document)
    {
        if (_lastFrame is { } frame && ReferenceEquals(document, _metricsDocument) && _metricsObserver is { } observer &&
            _metricsViewport == (RenderDevice.ViewPortWidth, RenderDevice.ViewPortHeight))
        {
            KeepMutations(observer.Flush());
            var scrollTop = document.DocumentElement is { } root ? GetScrollTop(root, double.MaxValue) : 0d;

            if (_pendingMutations.Count == 0 && ReferenceEquals(_metrics, _lastFrameLayout) && (frame.CanReposition || scrollTop == frame.ScrollTop))
            {
                return frame;
            }
        }

        PrepareFrame();
        return _lastFrame!;
    }

    /// <summary>Gets the frame with the selection shown.</summary>
    private RenderedFrame ShowSelection(RenderedFrame frame)
    {
        if (!_isTextSelectionEnabled || Context.Active is not { } document || Selection.TryGet(document) is not { Range: { IsCollapsed: false } range })
        {
            _shownSelection = null;
            return frame;
        }

        var selection = frame.TextMap.Resolve(range);

        if (_shownSelection is { } shown && ReferenceEquals(shown.Base, frame) && shown.Selection == selection)
        {
            return shown.Shown;
        }

        var painted = frame.WithSelection(HtmlRenderer.PaintSelection(frame.DisplayList, frame.TextMap, selection));
        _shownSelection = (frame, painted, selection);
        return painted;
    }
}
