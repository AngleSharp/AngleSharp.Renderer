namespace AngleSharp.Dom;

using AngleSharp.Browser;
using AngleSharp.Renderer;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs the observers of the layout of a document: after anything that may have changed a box, once
/// per turn of the event loop, as scrolling alone may not even need a layout.
/// </summary>
internal sealed class LayoutObserverScheduler
{
    private const int FrameInterval = 16;

    private static readonly ConditionalWeakTable<IDocument, LayoutObserverScheduler> Schedulers = [];

    private readonly IDocument _document;
    private readonly List<IntersectionObserver> _intersectionObservers = [];
    private readonly List<ResizeObserver> _resizeObservers = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly EventHandler _onChanged;
    private readonly List<InteractiveHtmlRendererState> _states = [];
    private readonly MutationObserver _mutations;
    private bool _isAttached;
    private bool _isRunning;
    private int _isScheduled;
    private int _isDelayed;
    private long _lastRun;

    private LayoutObserverScheduler(IDocument document)
    {
        _document = document;
        _onChanged = (_, _) => Schedule();
        _mutations = new MutationObserver((_, _) => ScheduleForMutations());
    }

    public static LayoutObserverScheduler For(IDocument document) =>
        Schedulers.GetValue(document, static key => new LayoutObserverScheduler(key));

    public void Add(IntersectionObserver observer)
    {
        if (!_intersectionObservers.Contains(observer))
        {
            _intersectionObservers.Add(observer);
        }

        Attach();
    }

    public void Add(ResizeObserver observer)
    {
        if (!_resizeObservers.Contains(observer))
        {
            _resizeObservers.Add(observer);
        }

        Attach();
    }

    private void Attach()
    {
        if (!_isAttached)
        {
            _isAttached = true;

            // what is around a frame moves it as well
            for (var context = _document.Context; context is not null; context = context.Parent)
            {
                if (context.GetDomHarness() is InteractiveHtmlRendererState state)
                {
                    _states.Add(state);
                    state.PaintInvalidated += _onChanged;
                    state.FrameUpdated += _onChanged;
                }
            }

            _mutations.Connect(_document, childList: true, subtree: true, attributes: true, characterData: true);
        }
    }

    public void Remove(IntersectionObserver observer)
    {
        _intersectionObservers.Remove(observer);
        DetachIfUnused();
    }

    public void Remove(ResizeObserver observer)
    {
        _resizeObservers.Remove(observer);
        DetachIfUnused();
    }

    private void DetachIfUnused()
    {
        if (_intersectionObservers.Count == 0 && _resizeObservers.Count == 0)
        {
            Detach();
        }
    }

    public void Schedule()
    {
        if (Interlocked.Exchange(ref _isScheduled, 1) == 0)
        {
            if (_document.Context.GetService<IEventLoop>() is { } loop)
            {
                loop.Enqueue(_ => Run(), TaskPriority.Normal);
            }
            else
            {
                Run();
            }
        }
    }

    private void Run()
    {
        Volatile.Write(ref _isScheduled, 0);

        // Without an event loop, what a callback does ends up here right away. Going on from here
        // would never end for callbacks that never settle: the next change picks it up, as the
        // next turn of an event loop would.
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;

        try
        {
            RunObservers();
        }
        finally
        {
            _isRunning = false;
        }
    }

    private bool HasEventLoop => _document.Context.GetService<IEventLoop>() is not null;

    // The layout of what is around a frame is what gives the frame the size of its viewport, so it
    // has to come first for what is measured in the frame to mean anything.
    private void LayOutAncestors()
    {
        var ancestors = new Stack<IDocument>();

        for (var context = _document.Context.Parent; context is not null; context = context.Parent)
        {
            if (context.Active is { } active)
            {
                ancestors.Push(active);
            }
        }

        foreach (var ancestor in ancestors)
        {
            if (ancestor.Context.GetDomHarness() is InteractiveHtmlRendererState state)
            {
                state.GetLayoutSnapshot(ancestor);
            }
        }
    }

    private void RunObservers()
    {
        Volatile.Write(ref _lastRun, Environment.TickCount64);

        // the document is gone, and with it everything it observed
        if (!ReferenceEquals(_document.Context.Active, _document))
        {
            Detach();
            return;
        }

        LayOutAncestors();

        // sizes first, as what a callback makes of them is what the intersections are about, and
        // what is left is for the next turn, which never comes without an event loop to take it
        if (_resizeObservers.Count > 0 && ResizeObserver.Deliver(_document, _resizeObservers.ToArray()) && HasEventLoop)
        {
            ScheduleForMutations();
        }

        var time = _clock.Elapsed.TotalMilliseconds;

        foreach (var observer in _intersectionObservers.ToArray())
        {
            observer.Update(time);
        }
    }

    // Changes of the document may come in a stream, and each look at them is a layout. A browser
    // would look once per frame, so that is what this does, too, whether or not frames are made.
    internal void ScheduleForMutations()
    {
        var waited = Environment.TickCount64 - Volatile.Read(ref _lastRun);

        // without an event loop nothing may go to another thread, as that would touch the DOM there
        if (waited >= FrameInterval || !HasEventLoop)
        {
            Schedule();
        }
        else if (Interlocked.Exchange(ref _isDelayed, 1) == 0)
        {
            Task.Delay((int)(FrameInterval - waited)).ContinueWith(_ =>
            {
                Volatile.Write(ref _isDelayed, 0);
                Schedule();
            }, TaskScheduler.Default);
        }
    }

    private void Detach()
    {
        foreach (var state in _states)
        {
            state.PaintInvalidated -= _onChanged;
            state.FrameUpdated -= _onChanged;
        }

        _states.Clear();
        _mutations.Disconnect();
        _isAttached = false;
    }
}
