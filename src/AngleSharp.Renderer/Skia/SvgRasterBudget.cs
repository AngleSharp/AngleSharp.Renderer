namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Limits the memory of the rasters of all SVGs in the process, dropping the least recently used
/// ones. It only keeps the books: the rasters belong to their images and die with them, so a closed
/// document's SVGs are never kept alive by being in here.
/// </summary>
internal static class SvgRasterBudget
{
    // Room for 8 rasters of the largest cached size (SvgVectorImage.MaxCachedRasterPixels), so a
    // raster being added is never the one to go. When a page needs more than this at once, its
    // images are simply rendered again, which is as slow as not caching them.
    public const long LimitBytes = 128L * 1024L * 1024L;

    private static readonly LinkedList<Ticket> s_tickets = [];

    /// <summary>
    /// The bytes of the rasters currently accounted for.
    /// </summary>
    public static long UsedBytes { get; private set; }

    /// <summary>
    /// Accounts for a new raster.
    /// </summary>
    /// <returns>
    /// The tickets of the rasters that have to go. The caller has to tell their owners without
    /// holding a lock of an image: two images evicting each other's rasters would deadlock.
    /// </returns>
    public static List<Ticket> Add(Ticket ticket)
    {
        var evicted = new List<Ticket>();

        lock (s_tickets)
        {
            s_tickets.AddLast(ticket.Node);
            UsedBytes += ticket.Bytes;

            if (UsedBytes > LimitBytes)
            {
                ForgetCollectedOwners();
            }

            while (UsedBytes > LimitBytes)
            {
                var oldest = s_tickets.First!.Value;
                Forget(oldest);
                evicted.Add(oldest);
            }
        }

        return evicted;
    }

    /// <summary>
    /// Marks a raster as the most recently used one.
    /// </summary>
    public static void Touch(Ticket ticket)
    {
        lock (s_tickets)
        {
            if (ticket.Node.List is not null)
            {
                s_tickets.Remove(ticket.Node);
                s_tickets.AddLast(ticket.Node);
            }
        }
    }

    /// <summary>
    /// Stops accounting for a raster its owner dropped.
    /// </summary>
    public static void Remove(Ticket ticket)
    {
        lock (s_tickets)
        {
            if (ticket.Node.List is not null)
            {
                Forget(ticket);
            }
        }
    }

    /// <summary>
    /// The rasters of collected images are gone already, and must not push out those still in use.
    /// </summary>
    private static void ForgetCollectedOwners()
    {
        for (var node = s_tickets.First; node is not null;)
        {
            var next = node.Next;

            if (!node.Value.Owner.TryGetTarget(out _))
            {
                Forget(node.Value);
            }

            node = next;
        }
    }

    private static void Forget(Ticket ticket)
    {
        s_tickets.Remove(ticket.Node);
        UsedBytes -= ticket.Bytes;
    }

    /// <summary>
    /// The entry of one raster in the books.
    /// </summary>
    internal sealed class Ticket
    {
        public Ticket(SvgVectorImage owner, long bytes)
        {
            Owner = new WeakReference<SvgVectorImage>(owner);
            Bytes = bytes;
            Node = new LinkedListNode<Ticket>(this);
        }

        public WeakReference<SvgVectorImage> Owner { get; }

        public long Bytes { get; }

        public LinkedListNode<Ticket> Node { get; }
    }
}
