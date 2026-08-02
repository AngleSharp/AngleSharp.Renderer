namespace AngleSharp.Dom;

using AngleSharp.Dom.Geometry;

internal sealed class CaretPosition : ICaretPosition
{
    private readonly IDomRect _clientRect;

    public CaretPosition(INode offsetNode, int offset, IDomRect clientRect)
    {
        ArgumentNullException.ThrowIfNull(offsetNode);
        ArgumentNullException.ThrowIfNull(clientRect);

        OffsetNode = offsetNode;
        Offset = Math.Max(0, offset);
        _clientRect = clientRect;
    }

    public INode OffsetNode { get; }

    public int Offset { get; }

    public IDomRect GetClientRect() => _clientRect;
}
