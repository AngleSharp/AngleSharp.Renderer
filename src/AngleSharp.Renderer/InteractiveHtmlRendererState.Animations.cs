namespace AngleSharp.Renderer;

using AngleSharp.Browser;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System.Linq;

internal sealed partial class InteractiveHtmlRendererState
{
    private readonly List<AngleSharp.Dom.Animation> _scriptAnimations = [];

    internal void InvalidateAnimation() => OnPaintInvalidated();

    /// <summary>
    /// Tells that a canvas was drawn into: the frame shows it once it is rasterized again, as
    /// its boxes are where they were.
    /// </summary>
    internal void OnCanvasChanged() => OnPaintInvalidated(keepsMetrics: true);

    /// <summary>
    /// Follows the CSS animations of an element whose style was computed for painting, to give
    /// it the events of animations starting, repeating, ending or being cancelled.
    /// </summary>
    internal void ObserveAnimations(IElement element, ICssStyleDeclaration computedStyle, bool isAnimated)
    {
        if (element is not IPseudoElement)
        {
            _animationTracker.Observe(element, computedStyle, isAnimated, _clockMs);
        }
    }

    /// <summary>
    /// Follows the CSS transitions of an element whose style was computed for painting, to
    /// start the ones of properties whose values changed (e.g. by a script changing a class).
    /// </summary>
    internal void ObserveTransitions(IElement element, ICssStyleDeclaration computedStyle, string? authoredTransform)
    {
        // what is not rendered has no transition, before or after (its values are forgotten)
        if (element is not IPseudoElement && CssTransitionTracker.HasTransition(computedStyle) && !IsNotRendered(computedStyle))
        {
            var previousStyle = _lastFrameLayout?.GetComputedStyle(element);
            _transitionTracker.Observe(element, computedStyle, previousStyle is not null && !IsNotRendered(previousStyle) ? previousStyle : null, authoredTransform);
        }

        static bool IsNotRendered(ICssStyleDeclaration style) => style.GetPropertyValue("display") is "none";
    }

    // The events go to script once the frame that found them is done, as a task of the event
    // loop, not in the middle of laying out the page.
    private void DispatchAnimationEvents()
    {
        var animationEvents = _animationTracker.TakeEvents();
        var transitionEvents = _transitionTracker.TakeEvents();

        if (animationEvents.Count == 0 && transitionEvents.Count == 0)
        {
            return;
        }

        void Run()
        {
            foreach (var record in transitionEvents)
            {
                if (record.Target.IsConnected())
                {
                    record.Target.Dispatch(new TransitionEvent(record.Type, bubbles: true, cancelable: false, record.PropertyName, record.ElapsedSeconds));
                }
            }

            foreach (var record in animationEvents)
            {
                if (record.Target.IsConnected())
                {
                    record.Target.Dispatch(new AnimationEvent(record.Type, bubbles: true, cancelable: false, record.AnimationName, record.ElapsedSeconds));
                }
            }
        }

        if (Context.GetService<IEventLoop>() is { } loop)
        {
            loop.Enqueue(_ => Run(), TaskPriority.Normal);
        }
        else
        {
            Run();
        }
    }

    internal AngleSharp.Dom.Animation CreateAnimation(AngleSharp.Dom.KeyframeEffect effect)
    {
        var animation = new AngleSharp.Dom.Animation(this, effect);
        _scriptAnimations.Add(animation);
        animation.Play();
        return animation;
    }

    internal AngleSharp.Dom.Animation[] GetAnimations(IElement? element = null) =>
        _scriptAnimations
            .Where(animation => element is null || ReferenceEquals(animation.Effect.Target, element))
            .ToArray();

    internal void RemoveAnimation(AngleSharp.Dom.Animation animation) => _scriptAnimations.Remove(animation);

    internal IReadOnlyList<string> GetScriptAnimatedProperties(IElement element) =>
        _scriptAnimations
            .Where(animation => ReferenceEquals(animation.Effect.Target, element) && animation.IsContributing)
            .SelectMany(animation => animation.AnimatedProperties)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal string? GetScriptAnimatedValue(IElement element, string property, ICssStyleDeclaration style)
    {
        string? value = null;
        foreach (var animation in _scriptAnimations.ToArray())
        {
            if (ReferenceEquals(animation.Effect.Target, element))
            {
                animation.OnTimeAdvanced();
                value = animation.GetAnimatedValue(property, _clockMs, style) ?? value;
            }
        }

        return value;
    }

    /// <summary>
    /// Gets if a transition or a script's animation may give an element another value than its
    /// computed style has (CSS animations are told by the style itself).
    /// </summary>
    internal bool MayOverrideComputedStyles => _transitionTracker.HasRuns || _scriptAnimations.Count > 0;

    private bool HasRunningScriptAnimations
    {
        get
        {
            foreach (var animation in _scriptAnimations.ToArray())
            {
                animation.OnTimeAdvanced();
                var target = animation.Effect.Target;
                if (animation.IsRunning &&
                    target.IsConnected() &&
                    target.ComputeCurrentStyle()?.GetPropertyValue("display") is not "none")
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void AdvanceScriptAnimations()
    {
        foreach (var animation in _scriptAnimations.ToArray())
        {
            animation.OnTimeAdvanced();
        }
    }
}
