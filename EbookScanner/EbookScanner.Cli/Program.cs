using EbookScanner.Core;
using EbookScanner.Core.Formatters;
using EbookScanner.Core.Models;
using ModelContextProtocol.Server;
using System.CommandLine;
using System.ComponentModel;

// ── root command (scan) ────────────────────────────────────────────────────────

var directoryArg = new Argument<string?>("directory")
{
    Description = "Directory to scan (default: current directory)",
    Arity = ArgumentArity.ZeroOrOne,
    DefaultValueFactory = _ => null,
};

var outputOption = new Option<string?>("--output", ["-o"])
{
    Description = "Write output to <file> instead of stdout",
};

var formatOption = new Option<string>("--format", ["-f"])
{
    Description = "Output format: markdown, json, or table (default: markdown)",
    DefaultValueFactory = _ => "markdown",
};

var recursiveOption = new Option<bool>("--recursive", ["-r"])
{
    Description = "Scan subdirectories recursively",
    DefaultValueFactory = _ => false,
};

var includeOption = new Option<string[]?>("--include")
{
    Description = "Formats to include: pdf, epub, mobi (default: all)",
    AllowMultipleArgumentsPerToken = true,
};

var columnsOption = new Option<string[]?>("--columns")
{
    Description = $"Columns to include in table output (only used with --format table). " +
                  $"Available: {string.Join(", ", MarkdownTableFormatter.ValidColumns)}. " +
                  $"Default: {string.Join(", ", MarkdownTableFormatter.DefaultColumns)}.",
    AllowMultipleArgumentsPerToken = true,
};

var rootCommand = new RootCommand("Scan a directory for ebooks and extract metadata.")
{
    directoryArg,
    outputOption,
    formatOption,
    recursiveOption,
    includeOption,
    columnsOption,
};

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var directory = parseResult.GetValue(directoryArg) ?? Directory.GetCurrentDirectory();
    var outputFile = parseResult.GetValue(outputOption);
    var format = parseResult.GetValue(formatOption) ?? "markdown";
    var recursive = parseResult.GetValue(recursiveOption);
    var include = parseResult.GetValue(includeOption);
    var columns = parseResult.GetValue(columnsOption);

    if (!Directory.Exists(directory))
    {
        Console.Error.WriteLine($"error: directory not found: {directory}");
        Environment.Exit(1);
        return;
    }

    BookFormat[]? formats = null;
    if (include is { Length: > 0 })
    {
        var parsed = new List<BookFormat>();
        foreach (var fmt in include)
        {
            if (Enum.TryParse<BookFormat>(fmt, ignoreCase: true, out var bf))
                parsed.Add(bf);
            else
                Console.Error.WriteLine($"warning: unknown format '{fmt}' ignored.");
        }
        formats = parsed.Count > 0 ? parsed.ToArray() : null;
    }

    var options = new ScanOptions(
        Directory: directory,
        Recursive: recursive,
        Formats: formats);

    var service = new EbookScannerService(onError: (file, ex) => Console.Error.WriteLine($"warning: {file}: {ex.Message}"));
    var scanProgress = new Progress<ScanProgress>(p => Console.Out.WriteLine($"[{p.Current}/{p.Total}] {p.FilePath}"));
    var result = await service.ScanAsync(options, scanProgress, cancellationToken);

    IMetadataFormatter formatter = format.ToLowerInvariant() switch
    {
        "json"  => new JsonFormatter(),
        "table" => new MarkdownTableFormatter(ParseColumns(columns)),
        _       => new MarkdownFormatter(),
    };

    var output = formatter.Format(result);

    if (outputFile is not null)
        await File.WriteAllTextAsync(outputFile, output, cancellationToken);
    else
        Console.Write(output);
});

// ── mcp subcommand ─────────────────────────────────────────────────────────────

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
        builder.Services.AddSingleton<EbookScannerService>();
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<EbookScannerTools>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.RunAsync($"http://127.0.0.1:{port}");
        return;
    }

    var hostBuilder = Host.CreateApplicationBuilder();
    hostBuilder.Logging.AddConsole(options =>
        options.LogToStandardErrorThreshold = LogLevel.Trace);
    hostBuilder.Services.AddSingleton<EbookScannerService>();
    hostBuilder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<EbookScannerTools>();

    await hostBuilder.Build().RunAsync(token: cancellationToken);
});

rootCommand.Add(mcpCommand);

return await rootCommand
    .Parse(args, new ParserConfiguration())
    .InvokeAsync(new InvocationConfiguration(), CancellationToken.None);

// ── helpers ────────────────────────────────────────────────────────────────────

static IReadOnlyList<string>? ParseColumns(string[]? columns)
{
    if (columns is not { Length: > 0 }) return null;
    var valid = new List<string>();
    foreach (var col in columns)
    {
        var normalized = col.Trim().ToLowerInvariant();
        if (MarkdownTableFormatter.ValidColumns.Contains(normalized))
            valid.Add(normalized);
        else
            Console.Error.WriteLine($"warning: unknown column '{col}' ignored. Valid columns: {string.Join(", ", MarkdownTableFormatter.ValidColumns)}");
    }
    return valid.Count > 0 ? valid : null;
}

// ── MCP tool definitions ───────────────────────────────────────────────────────

[McpServerToolType]
public sealed class EbookScannerTools(EbookScannerService service)
{
    [McpServerTool(Name = "scan_directory"),
     Description("Scan a directory for PDF, EPUB, and MOBI files and return their metadata as a catalog. " +
                 "The 'format' parameter accepts 'markdown' (default), 'json', or 'table'. " +
                 "When format is 'table', the optional 'columns' parameter accepts a comma-separated list of column names: " +
                 "name, location, format, authors, publisher, language, isbn, published, modified, pages, tags, size, description. " +
                 "Pass 'recursive' as true to also scan subdirectories.")]
    public async Task<string> ScanDirectory(
        string path,
        string format = "markdown",
        string? columns = null,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
            return $"error: directory not found: {path}";

        var options = new ScanOptions(Directory: path, Recursive: recursive);
        var result = await service.ScanAsync(options, cancellationToken: cancellationToken);

        IMetadataFormatter formatter = format.ToLowerInvariant() switch
        {
            "json"  => new JsonFormatter(),
            "table" => new MarkdownTableFormatter(ParseColumnsString(columns)),
            _       => new MarkdownFormatter(),
        };

        return formatter.Format(result);
    }

    [McpServerTool(Name = "extract_metadata"),
     Description("Extract metadata from a single PDF, EPUB, or MOBI file. " +
                 "The 'format' parameter accepts 'markdown', 'table', or 'json' (default). " +
                 "When format is 'table', the optional 'columns' parameter accepts a comma-separated list of column names: " +
                 "name, location, format, authors, publisher, language, isbn, published, modified, pages, tags, size, description.")]
    public async Task<string> ExtractMetadata(
        string filePath,
        string format = "json",
        string? columns = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return $"error: file not found: {filePath}";

        var metadata = await service.ExtractAsync(filePath, cancellationToken);
        if (metadata is null)
            return $"error: unsupported file format: {Path.GetExtension(filePath)}";

        if (format.Equals("markdown", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            var scanResult = new ScanResult(
                ScannedDirectory: Path.GetDirectoryName(filePath) ?? "",
                ScannedAt: DateTimeOffset.UtcNow,
                Books: [metadata]);

            IMetadataFormatter formatter = format.Equals("table", StringComparison.OrdinalIgnoreCase)
                ? new MarkdownTableFormatter(ParseColumnsString(columns))
                : new MarkdownFormatter();

            return formatter.Format(scanResult);
        }

        return System.Text.Json.JsonSerializer.Serialize(
            metadata, EbookScannerJsonContext.Default.BookMetadata);
    }

    private static IReadOnlyList<string>? ParseColumnsString(string? columns)
    {
        if (string.IsNullOrWhiteSpace(columns)) return null;
        var valid = new List<string>();
        foreach (var col in columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = col.ToLowerInvariant();
            if (MarkdownTableFormatter.ValidColumns.Contains(normalized))
                valid.Add(normalized);
        }
        return valid.Count > 0 ? valid : null;
    }
}
