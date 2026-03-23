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

var rootCommand = new RootCommand("Convert documents and URLs to Markdown.")
{
    inputArg,
    outputOption,
    inputNameOption,
    mimeTypeOption,
};

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var input = parseResult.GetValue(inputArg);
    var output = parseResult.GetValue(outputOption);
    var inputName = parseResult.GetValue(inputNameOption);
    var mimeType = parseResult.GetValue(mimeTypeOption);

    using var service = new MarkItDownService();
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

var mcpCommand = new Command("mcp", "Start a Model Context Protocol (MCP) server.")
{
    httpOption,
    portOption,
};

mcpCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var useHttp = parseResult.GetValue(httpOption);
    var port = parseResult.GetValue(portOption) ?? 3001;

    if (useHttp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<MarkItDownService>();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<MarkItDownTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.RunAsync($"http://127.0.0.1:{port}");
        return;
    }

    var hostBuilder = Host.CreateApplicationBuilder();
    hostBuilder.Logging.AddConsole(options =>
        options.LogToStandardErrorThreshold = LogLevel.Trace);
    hostBuilder.Services.AddSingleton<MarkItDownService>();
    hostBuilder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<MarkItDownTools>();

    await hostBuilder.Build().RunAsync(token: cancellationToken);
});

rootCommand.Add(mcpCommand);

return await rootCommand
    .Parse(args, new ParserConfiguration())
    .InvokeAsync(new InvocationConfiguration(), CancellationToken.None);

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
