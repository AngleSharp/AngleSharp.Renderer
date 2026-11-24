namespace AngleSharp;

using AngleSharp.Renderer.Controls;
using System.Linq;

/// <summary>
/// Configuration extensions for choosing how inputs (e.g. `color` or `date`) are shown.
/// </summary>
public static class InputControlConfigurationExtensions
{
    /// <summary>
    /// Shows inputs with the controls of the given provider in place of the built-in ones.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="provider">The provider, e.g. an <see cref="InputControlRegistry"/>.</param>
    /// <returns>The configuration.</returns>
    public static IConfiguration WithInputControls(this IConfiguration configuration, IInputControlProvider provider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(provider);
        return configuration.WithOnly(provider);
    }

    /// <summary>
    /// Shows inputs of a type with the given control, the others with the built-in controls (or
    /// those registered before).
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="type">The input type, e.g. "color".</param>
    /// <param name="control">The control.</param>
    /// <returns>The configuration.</returns>
    public static IConfiguration WithInputControl(this IConfiguration configuration, string type, IInputControl control)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // a copy, so configurations derived from the same one don't share their controls
        var registry = configuration.Services.OfType<InputControlRegistry>().FirstOrDefault()?.Clone() ?? InputControlRegistry.CreateDefault();
        registry.Register(type, control);
        return configuration.WithOnly<IInputControlProvider>(registry);
    }
}
