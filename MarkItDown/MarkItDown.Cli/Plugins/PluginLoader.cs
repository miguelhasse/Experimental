using MarkItDown.Core;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MarkItDown.Cli.Plugins;

internal static class PluginLoader
{
    /// <summary>
    /// Resolves the plugins directory in priority order:
    /// CLI arg → MARKITDOWN_PLUGINS_DIR env var → {exe}/plugins/
    /// </summary>
    public static string ResolvePluginsDirectory(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var fromEnv = Environment.GetEnvironmentVariable("MARKITDOWN_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var exeDir = AppContext.BaseDirectory;
        return Path.Combine(exeDir, "plugins");
    }

    /// <summary>
    /// Returns true if the runtime supports dynamic code execution (i.e. not AOT).
    /// When false, plugin loading is a no-op.
    /// </summary>
    public static bool IsDynamicCodeSupported =>
        RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// Scans <paramref name="pluginsDir"/> for *.dll files, discovers
    /// <see cref="IMarkItDownPlugin"/> implementations, and calls
    /// <see cref="IMarkItDownPlugin.RegisterConverters"/> on each one.
    /// Logs a warning and continues if an individual plugin fails to load —
    /// consistent with the Python upstream's warn-and-continue behaviour.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "Plugin loading uses AssemblyLoadContext and runtime reflection.")]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "Plugin assemblies are not known at compile time.")]
    public static void LoadPlugins(
        MarkItDownService service,
        string pluginsDir,
        ILogger? logger = null)
    {
        if (!Directory.Exists(pluginsDir))
        {
            logger?.LogDebug(
                "Plugin directory '{Dir}' does not exist — no plugins loaded.", pluginsDir);
            return;
        }

        foreach (var dll in Directory.EnumerateFiles(
                     pluginsDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var context = new PluginLoadContext(dll);
                var assembly = context.LoadFromAssemblyName(
                    new AssemblyName(Path.GetFileNameWithoutExtension(dll)));

                foreach (var type in assembly.GetTypes().Where(t =>
                    typeof(IMarkItDownPlugin).IsAssignableFrom(t)
                    && t is { IsClass: true, IsAbstract: false }))
                {
                    var plugin = (IMarkItDownPlugin)Activator.CreateInstance(type)!;
                    plugin.RegisterConverters(service);
                    logger?.LogInformation(
                        "Loaded plugin '{Name}' from '{File}'.",
                        plugin.Name, Path.GetFileName(dll));
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Plugin '{File}' failed to load — skipping.", Path.GetFileName(dll));
            }
        }
    }
}
