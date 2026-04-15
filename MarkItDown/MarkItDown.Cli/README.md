# MarkItDown.Cli

A command-line tool that converts any supported document or URL to Markdown and writes the result to stdout or a file. Publishes as a self-contained **Native AOT** executable with no .NET runtime dependency.

**Target:** `net10.0` · **SDK:** `Microsoft.NET.Sdk.Web` · **Native AOT:** `PublishAot=true` (default) / JIT with `-p:EnablePlugins=true`

---

## Build & run

```powershell
# Run directly with `dotnet run`
dotnet run --project MarkItDown.Cli -- <arguments>

# Build (JIT)
dotnet build MarkItDown.Cli

# Publish as Native AOT (~65 MB self-contained exe)
dotnet publish MarkItDown.Cli -c Release

# Publish as JIT with plugin support (~35 MB self-contained exe)
dotnet publish MarkItDown.Cli -c Release -p:EnablePlugins=true
```

---

## Usage

```
markitdown [<input>] [command] [options]
markitdown mcp [options]
```

The default (root) command converts a document or URL to Markdown.  
The `mcp` subcommand starts a [Model Context Protocol](https://modelcontextprotocol.io) server.

### Root command — convert

When `<input>` is omitted the tool reads from **stdin**. Use `--input-name` or `--mime-type` to give format hints when piping binary data.

#### Positional argument

| Argument | Description |
|---|---|
| `<input>` | File path, `http://`/`https://` URL, `file://` URI, `data:` URI, or `-` for stdin |

#### Options

| Flag | Alias | Description |
|---|---|---|
| `--output <file>` | `-o` | Write Markdown to `<file>` instead of stdout |
| `--input-name <name>` | | Hint filename (used to infer MIME type / extension) |
| `--mime-type <type>` | | Override MIME type (e.g. `application/pdf`) |
| `--enable-plugins` | | Load additional converters from the plugins directory |
| `--plugins-dir <path>` | | Plugins directory path (default: `{exe}/plugins/`; overrides `MARKITDOWN_PLUGINS_DIR`) |
| `--version` | | Print version and exit |
| `--help` | `-h`, `-?` | Print usage and exit |

### `mcp` subcommand — MCP server

Starts a Model Context Protocol server exposing a single tool: **`convert_to_markdown`**.

#### Options

| Flag | Default | Description |
|---|---|---|
| `--http` | — | Use HTTP/SSE transport instead of stdio |
| `--port <port>` | `3001` | TCP port for HTTP transport |
| `--enable-plugins` | — | Load additional converters from the plugins directory |
| `--plugins-dir <path>` | `{exe}/plugins/` | Plugins directory; overrides `MARKITDOWN_PLUGINS_DIR` env var |
| `--help` | | Print help and exit |

Plugins can also be enabled without modifying the command line via environment variables (useful for MCP host configs):

| Environment variable | Purpose |
|---|---|
| `MARKITDOWN_ENABLE_PLUGINS=true` | Equivalent to `--enable-plugins` |
| `MARKITDOWN_PLUGINS_DIR=<path>` | Equivalent to `--plugins-dir` |

---

## Examples

### Convert a local file

```powershell
dotnet run --project MarkItDown.Cli -- report.pdf
dotnet run --project MarkItDown.Cli -- presentation.pptx -o slides.md
```

### Convert a URL

```powershell
dotnet run --project MarkItDown.Cli -- https://en.wikipedia.org/wiki/Markdown
dotnet run --project MarkItDown.Cli -- https://example.com/feed.rss -o feed.md
```

### Pipe from stdin

```powershell
# HTML from stdin — let the tool infer format from the filename hint
Get-Content page.html | dotnet run --project MarkItDown.Cli -- --input-name page.html

# Explicit MIME type override
Get-Content data.csv | dotnet run --project MarkItDown.Cli -- --mime-type text/csv

# Binary data through stdin requires --mime-type
[System.IO.File]::ReadAllBytes("doc.docx") | dotnet run --project MarkItDown.Cli -- --mime-type application/vnd.openxmlformats-officedocument.wordprocessingml.document
```

### Load plugins

Plugins are DLLs placed in the plugins directory. Pass `--enable-plugins` to activate them:

```powershell
# Load from default {exe}/plugins/ directory
dotnet run --project MarkItDown.Cli -- report.pdf --enable-plugins

# Load from a custom directory
dotnet run --project MarkItDown.Cli -- report.pdf --enable-plugins --plugins-dir ~/.markitdown/plugins
```

Plugin loading requires a JIT (non-AOT) binary. Publish with `-p:EnablePlugins=true` to get a plugin-capable build:

```powershell
dotnet publish MarkItDown.Cli -c Release -p:EnablePlugins=true
.\bin\Release\net10.0\win-x64\publish\MarkItDown.Cli.exe report.pdf --enable-plugins
```

### Use the AOT binary directly

After publishing:

```powershell
.\bin\Release\net10.0\win-x64\publish\MarkItDown.Cli.exe report.xlsx -o report.md
.\bin\Release\net10.0\win-x64\publish\MarkItDown.Cli.exe https://en.wikipedia.org/wiki/C_Sharp_(programming_language)
```

### Start the MCP server

```powershell
# stdio transport (default — for MCP client integration)
dotnet run --project MarkItDown.Cli -- mcp

# HTTP transport on a specific port
dotnet run --project MarkItDown.Cli -- mcp --http --port 3001
# Endpoint: http://localhost:3001/mcp

# AOT binary — stdio
.\bin\Release\net10.0\win-x64\publish\MarkItDown.Cli.exe mcp
```

---

## MCP tool reference

### `convert_to_markdown`

Converts a document or URL to Markdown.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `uri` | `string` | ✅ | A `file://`, `http://`, `https://`, or `data:` URI pointing to the document |

**Returns:** Markdown text as a plain string.

#### Example invocation (MCP JSON-RPC)

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "convert_to_markdown",
    "arguments": {
      "uri": "https://en.wikipedia.org/wiki/Markdown"
    }
  }
}
```

---

## MCP client configuration

### Claude Desktop (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "markitdown": {
      "command": "C:\\path\\to\\MarkItDown.Cli.exe",
      "args": ["mcp"]
    }
  }
}
```

With plugins enabled via environment variable (requires a JIT/plugin-capable build):

```json
{
  "mcpServers": {
    "markitdown": {
      "command": "C:\\path\\to\\MarkItDown.Cli.exe",
      "args": ["mcp"],
      "env": {
        "MARKITDOWN_ENABLE_PLUGINS": "true",
        "MARKITDOWN_PLUGINS_DIR": "C:\\Users\\me\\.markitdown\\plugins"
      }
    }
  }
}
```

Or with `dotnet run` (during development):

```json
{
  "mcpServers": {
    "markitdown": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\MarkItDown.Cli", "--", "mcp"]
    }
  }
}
```

### VS Code (`settings.json` / workspace MCP config)

```json
{
  "mcp": {
    "servers": {
      "markitdown": {
        "type": "stdio",
        "command": "C:\\path\\to\\MarkItDown.Cli.exe",
        "args": ["mcp"]
      }
    }
  }
}
```

### HTTP transport (any MCP client supporting SSE/HTTP)

```json
{
  "mcpServers": {
    "markitdown": {
      "type": "http",
      "url": "http://localhost:3001/mcp"
    }
  }
}
```

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Unknown argument / bad usage |
| `2` | Conversion error (unsupported format or I/O failure) |

---

## Notes

- When `<input>` is `-` or omitted the tool reads stdin and closes the stream on EOF before converting.
- `--input-name` and `--mime-type` can be combined; `--mime-type` takes precedence for MIME resolution, `--input-name` also sets the extension.
- The tool creates an `HttpClient` and `MarkItDownService` for the duration of the run, then disposes them.
- For the `mcp` subcommand, `MarkItDownService` is registered as a **singleton** in the DI container; one `HttpClient` is shared for the lifetime of the process.
- MCP tool methods are registered with `WithTools<MarkItDownTools>()` — this avoids `IL2026` reflection warnings and is required for Native AOT compatibility.
- Plugin loading uses `AssemblyLoadContext` and is not compatible with Native AOT. Pass `-p:EnablePlugins=true` to `dotnet publish` to produce a JIT binary that supports plugins.
- `MARKITDOWN_ENABLE_PLUGINS` and `MARKITDOWN_PLUGINS_DIR` environment variables apply only to the `mcp` subcommand when running headlessly (e.g. from a Claude Desktop config). The `convert` command requires the explicit CLI flags.
