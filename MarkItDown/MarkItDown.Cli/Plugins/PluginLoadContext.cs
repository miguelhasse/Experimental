using System.Reflection;
using System.Runtime.Loader;

namespace MarkItDown.Cli.Plugins;

/// <summary>
/// Loads a plugin assembly in its own isolated load context so that its NuGet
/// dependencies do not conflict with those of other plugins or the host process.
/// </summary>
[System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
    "Plugin loading uses AssemblyLoadContext and runtime reflection.")]
[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
    "Plugin assemblies are not known at compile time.")]
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Let the host's default context own MarkItDown.Core so that
        // IMarkItDownPlugin types are unified across host and plugin.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
