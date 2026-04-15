namespace MarkItDown.Core;

/// <summary>
/// Implemented by third-party plugin assemblies to register custom converters with
/// MarkItDownService. The CLI discovers classes implementing this interface by scanning
/// DLL files in the plugins directory (default: {exe}/plugins/).
/// </summary>
/// <remarks>
/// Plugin assemblies should reference MarkItDown.Core with
/// <c>&lt;Private&gt;false&lt;/Private&gt;</c> and <c>&lt;ExcludeAssets&gt;runtime&lt;/ExcludeAssets&gt;</c>
/// to prevent type identity mismatches at runtime.
/// </remarks>
public interface IMarkItDownPlugin
{
    /// <summary>Display name shown in diagnostic output when the plugin is loaded.</summary>
    string Name { get; }

    /// <summary>
    /// Called once at startup to register one or more <see cref="Converters.DocumentConverter"/>
    /// instances with the service.
    /// </summary>
    void RegisterConverters(MarkItDownService service);
}
