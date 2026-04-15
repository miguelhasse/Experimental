using MarkItDown.Cli.Plugins;
using MarkItDown.Core;
using MarkItDown.Core.Models;
using ModelContextProtocol.Server;
using System.CommandLine;
using System.ComponentModel;

// ── root command (convert) ────────────────────────────────────────────────────

var inputArg = new Argument<string?>("input")
{
    Description = "Input file path, URI (http/https/file/data), or '-' for stdin",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => null,
};

var outputOption = new Option<string?>("--output", ["-o"])
{
    Description = "Write Markdown output to <file> instead of stdout",
};

var inputNameOption = new Option<string?>("--input-name")
{
    Description = "Filename hint used when reading from stdin",
};

var mimeTypeOption = new Option<string?>("--mime-type")
{
    Description = "MIME type hint used when reading from stdin",
};

var enablePluginsOption = new Option<bool>("--enable-plugins")
{
    Description = "Load additional converters from the plugins directory",
};

var pluginsDirOption = new Option<string?>("--plugins-dir")
{
    Description = "Path to plugins directory (default: {exe}/plugins/; overrides MARKITDOWN_PLUGINS_DIR)",
};

var rootCommand = new RootCommand("Convert documents and URLs to Markdown.")
{
    inputArg,
    outputOption,
    inputNameOption,
    mimeTypeOption,
    enablePluginsOption,
    pluginsDirOption,
};

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var input = parseResult.GetValue(inputArg);
    var output = parseResult.GetValue(outputOption);
    var inputName = parseResult.GetValue(inputNameOption);
    var mimeType = parseResult.GetValue(mimeTypeOption);
    var enablePlugins = parseResult.GetValue(enablePluginsOption);
    var pluginsDir = parseResult.GetValue(pluginsDirOption);

    using var service = BuildService(enablePlugins, pluginsDir);
    DocumentConverterResult result;

    if (input is not null && input != "-")
    {
        result = await service.ConvertAsync(input, cancellationToken: cancellationToken);
    }
    else
    {
        if (!Console.IsInputRedirected)
        {
            Console.Error.WriteLine("error: provide an input file/URI or pipe content to stdin.");
            Environment.Exit(1);
            return;
        }

        await using var stdin = Console.OpenStandardInput();
        result = await service.ConvertStreamAsync(
            stdin,
            new StreamInfo(
                FileName: inputName,
                Extension: Path.GetExtension(inputName ?? string.Empty),
                MimeType: mimeType),
            cancellationToken);
    }

    if (output is not null)
        await File.WriteAllTextAsync(output, result.Markdown, cancellationToken);
    else
        Console.WriteLine(result.Markdown);
});

// ── mcp subcommand ────────────────────────────────────────────────────────────

var httpOption = new Option<bool>("--http")
{
    Description = "Use HTTP/SSE transport instead of stdio",
};

var portOption = new Option<int?>("--port")
{
    Description = "TCP port for HTTP transport (default: 3001)",
};

var mcpEnablePluginsOption = new Option<bool>("--enable-plugins")
{
    Description = "Load additional converters from the plugins directory",
};

var mcpPluginsDirOption = new Option<string?>("--plugins-dir")
{
    Description = "Path to plugins directory (default: {exe}/plugins/; overrides MARKITDOWN_PLUGINS_DIR)",
};

var mcpCommand = new Command("mcp", "Start a Model Context Protocol (MCP) server.")
{
    httpOption,
    portOption,
    mcpEnablePluginsOption,
    mcpPluginsDirOption,
};

mcpCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var useHttp = parseResult.GetValue(httpOption);
    var port = parseResult.GetValue(portOption) ?? 3001;

    // Honour both the CLI flag and the env var (mirrors Python markitdown-mcp pattern).
    var enablePlugins = parseResult.GetValue(mcpEnablePluginsOption)
        || string.Equals(
            Environment.GetEnvironmentVariable("MARKITDOWN_ENABLE_PLUGINS"),
            "true", StringComparison.OrdinalIgnoreCase);
    var pluginsDir = parseResult.GetValue(mcpPluginsDirOption);

    if (useHttp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_ => BuildService(enablePlugins, pluginsDir));
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<MarkItDownTools>();

        var app = builder.Build();
        // Eagerly resolve the singleton so plugin load errors surface at startup.
        app.Services.GetRequiredService<MarkItDownService>();
        app.MapMcp("/mcp");
        await app.RunAsync($"http://127.0.0.1:{port}");
        return;
    }

    var hostBuilder = Host.CreateApplicationBuilder();
    hostBuilder.Logging.AddConsole(options =>
        options.LogToStandardErrorThreshold = LogLevel.Trace);
    hostBuilder.Services.AddSingleton(_ => BuildService(enablePlugins, pluginsDir));
    hostBuilder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<MarkItDownTools>();

    var host = hostBuilder.Build();
    // Eagerly resolve to surface plugin errors before accepting MCP connections.
    host.Services.GetRequiredService<MarkItDownService>();
    await host.RunAsync(token: cancellationToken);
});

rootCommand.Add(mcpCommand);

return await rootCommand
    .Parse(args, new ParserConfiguration())
    .InvokeAsync(new InvocationConfiguration(), CancellationToken.None);

// ── Shared service factory ────────────────────────────────────────────────────

#pragma warning disable IL3050, IL2026 // guarded by IsDynamicCodeSupported check inside PluginLoader
static MarkItDownService BuildService(bool enablePlugins, string? pluginsDir)
{
    var service = new MarkItDownService();

    if (enablePlugins)
    {
        if (!PluginLoader.IsDynamicCodeSupported)
        {
            Console.Error.WriteLine(
                "warning: --enable-plugins is not supported in AOT builds. " +
                "Rebuild without PublishAot (pass -p:EnablePlugins=true) for plugin support.");
        }
        else
        {
            var dir = PluginLoader.ResolvePluginsDirectory(pluginsDir);
            PluginLoader.LoadPlugins(service, dir);
        }
    }

    return service;
}
#pragma warning restore IL3050, IL2026

// ── MCP tool definitions ──────────────────────────────────────────────────────

[McpServerToolType]
public sealed class MarkItDownTools(MarkItDownService service)
{
    [McpServerTool(Name = "convert_to_markdown"),
     Description("Convert a file:, data:, http:, or https: URI to markdown.")]
    public async Task<string> ConvertToMarkdown(string uri, CancellationToken cancellationToken)
    {
        var result = await service.ConvertAsync(uri, cancellationToken: cancellationToken);
        return result.Markdown;
    }
}
