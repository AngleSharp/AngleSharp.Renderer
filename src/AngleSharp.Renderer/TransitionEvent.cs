namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Events;

/// <summary>
/// Represents an event of a CSS transition: transitionrun, transitionstart, transitionend or
/// transitioncancel.
/// </summary>
[DomName("TransitionEvent")]
public sealed class TransitionEvent : Event
{
    /// <summary>
    /// Creates a new transition event without a type.
    /// </summary>
    public TransitionEvent()
    {
        PropertyName = string.Empty;
        PseudoElement = string.Empty;
    }

    /// <summary>
    /// Creates a new transition event.
    /// </summary>
    /// <param name="type">The type of the event.</param>
    /// <param name="bubbles">If the event bubbles.</param>
    /// <param name="cancelable">If the event can be cancelled.</param>
    /// <param name="propertyName">The name of the property that transitions.</param>
    /// <param name="elapsedTime">The time the transition has been running, in seconds.</param>
    /// <param name="pseudoElement">The pseudo-element the transition runs on, or empty.</param>
    [DomConstructor]
    [DomInitDict(offset: 1, optional: true)]
    public TransitionEvent(string type, bool bubbles = false, bool cancelable = false, string propertyName = "", double elapsedTime = 0, string pseudoElement = "")
    {
        Init(type, bubbles, cancelable);
        PropertyName = propertyName;
        ElapsedTime = elapsedTime;
        PseudoElement = pseudoElement;
    }

    /// <summary>
    /// Gets the name of the property that transitions.
    /// </summary>
    [DomName("propertyName")]
    public string PropertyName { get; }

    /// <summary>
    /// Gets the time the transition has been running when the event fired, in seconds, without
    /// its delay.
    /// </summary>
    [DomName("elapsedTime")]
    public double ElapsedTime { get; }

    /// <summary>
    /// Gets the pseudo-element the transition runs on (e.g. ::before), or empty for the element.
    /// </summary>
    [DomName("pseudoElement")]
    public string PseudoElement { get; }
}
