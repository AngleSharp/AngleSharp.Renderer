namespace AngleSharp.Renderer;

using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System.Linq;

internal sealed partial class InteractiveHtmlRendererState
{
    private readonly List<AngleSharp.Dom.Animation> _scriptAnimations = [];

    internal void InvalidateAnimation() => OnPaintInvalidated();

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
