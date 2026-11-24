namespace AngleSharp.Renderer.Controls;

using AngleSharp.Html.Dom;
using System.Collections.Concurrent;

/// <summary>
/// Gives the <see cref="IInputControl"/> an input is shown with. Registered as a service of the
/// configuration (see <see cref="InputControlConfigurationExtensions"/>).
/// </summary>
public interface IInputControlProvider
{
    /// <summary>
    /// Gets the control of an input.
    /// </summary>
    /// <param name="input">The input element.</param>
    /// <returns>The control, or null for the built-in one of the type (if it has one).</returns>
    IInputControl? GetControl(IHtmlInputElement input);
}

/// <summary>
/// Controls per input type. The default one shows `color`, `date`, `datetime-local`, `month`,
/// `time` and `week` inputs with <see cref="ColorInputControl"/> and the
/// <see cref="DateTimeInputControl"/> of each type; a registry made with
/// <see cref="CreateDefault"/> can take others in their place.
/// </summary>
public sealed class InputControlRegistry : IInputControlProvider
{
    private readonly ConcurrentDictionary<string, IInputControl> _controls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the registry of the built-in controls, used when no other is registered.</summary>
    public static InputControlRegistry Default { get; } = CreateDefault();

    /// <summary>
    /// Creates a registry holding the built-in controls.
    /// </summary>
    /// <returns>A new registry that can be changed.</returns>
    public static InputControlRegistry CreateDefault()
    {
        var registry = new InputControlRegistry();
        registry.Register("color", new ColorInputControl());

        foreach (var type in DateTimeInputControl.Types)
        {
            registry.Register(type, new DateTimeInputControl(type));
        }

        return registry;
    }

    /// <summary>
    /// Creates a registry holding the same controls, which can be changed on its own.
    /// </summary>
    /// <returns>The copy.</returns>
    public InputControlRegistry Clone()
    {
        var registry = new InputControlRegistry();

        foreach (var (type, control) in _controls)
        {
            registry._controls[type] = control;
        }

        return registry;
    }

    /// <summary>Gets the input types having a control.</summary>
    public IEnumerable<string> Types => _controls.Keys;

    /// <summary>
    /// Shows inputs of a type with the given control, in place of any registered before.
    /// </summary>
    /// <param name="type">The input type, e.g. "date".</param>
    /// <param name="control">The control.</param>
    /// <returns>The registry.</returns>
    public InputControlRegistry Register(string type, IInputControl control)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(control);

        if (ReferenceEquals(this, Default))
        {
            throw new InvalidOperationException("The default registry cannot be changed; use CreateDefault() for one that can.");
        }

        _controls[type.Trim()] = control;
        return this;
    }

    /// <summary>
    /// Shows inputs of a type with the built-in control of the type again.
    /// </summary>
    /// <param name="type">The input type.</param>
    /// <returns>True if a control was registered for it.</returns>
    public bool Unregister(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (ReferenceEquals(this, Default))
        {
            throw new InvalidOperationException("The default registry cannot be changed; use CreateDefault() for one that can.");
        }

        return _controls.TryRemove(type.Trim(), out _);
    }

    /// <inheritdoc />
    public IInputControl? GetControl(IHtmlInputElement input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _controls.TryGetValue(input.Type?.Trim() ?? "text", out var control) ? control : null;
    }

    /// <summary>
    /// Gets the control an input is shown with in its browsing context.
    /// </summary>
    internal static IInputControl? Resolve(IHtmlInputElement input)
    {
        // a provider without a control for the type leaves it to the built-in one
        var provider = input.Owner?.Context?.GetService<IInputControlProvider>();
        return provider?.GetControl(input) ?? Default.GetControl(input);
    }
}
