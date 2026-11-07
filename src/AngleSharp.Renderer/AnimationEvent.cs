namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Events;

/// <summary>
/// Represents an event of a CSS animation: animationstart, animationiteration, animationend or
/// animationcancel.
/// </summary>
[DomName("AnimationEvent")]
public sealed class AnimationEvent : Event
{
    /// <summary>
    /// Creates a new animation event without a type.
    /// </summary>
    public AnimationEvent()
    {
        AnimationName = string.Empty;
        PseudoElement = string.Empty;
    }

    /// <summary>
    /// Creates a new animation event.
    /// </summary>
    /// <param name="type">The type of the event.</param>
    /// <param name="bubbles">If the event bubbles.</param>
    /// <param name="cancelable">If the event can be cancelled.</param>
    /// <param name="animationName">The value of animation-name the event belongs to.</param>
    /// <param name="elapsedTime">The time the animation has been running, in seconds.</param>
    /// <param name="pseudoElement">The pseudo-element the animation runs on, or empty.</param>
    [DomConstructor]
    [DomInitDict(offset: 1, optional: true)]
    public AnimationEvent(string type, bool bubbles = false, bool cancelable = false, string animationName = "", double elapsedTime = 0, string pseudoElement = "")
    {
        Init(type, bubbles, cancelable);
        AnimationName = animationName;
        ElapsedTime = elapsedTime;
        PseudoElement = pseudoElement;
    }

    /// <summary>
    /// Gets the value of animation-name the event belongs to.
    /// </summary>
    [DomName("animationName")]
    public string AnimationName { get; }

    /// <summary>
    /// Gets the time the animation has been running when the event fired, in seconds, without
    /// the time it was paused.
    /// </summary>
    [DomName("elapsedTime")]
    public double ElapsedTime { get; }

    /// <summary>
    /// Gets the pseudo-element the animation runs on (e.g. ::before), or empty for the element.
    /// </summary>
    [DomName("pseudoElement")]
    public string PseudoElement { get; }
}
