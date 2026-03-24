# EbookScanner.Cli

The CLI entry point for EbookScanner. Built on **System.CommandLine 2.0.5**. Supports scanning a directory for ebook metadata and running an **MCP (Model Context Protocol) server**.

## Usage

```
EbookScanner [<directory>] [options]
EbookScanner mcp [options]
```

## Scan Command (default)

Scans a directory for PDF, EPUB, MOBI, and CHM files and outputs a metadata catalog.

```bash
EbookScanner [<directory>] [-o <file>] [-f markdown|json] [-r] [--include pdf|epub|mobi|chm]
```

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `<directory>` | | Directory to scan | Current directory |
| `--output` | `-o` | Write output to file instead of stdout | stdout |
| `--format` | `-f` | Output format: `markdown` or `json` | `markdown` |
| `--recursive` | `-r` | Scan subdirectories recursively | false (top-level only) |
| `--include` | | Formats to include: `pdf`, `epub`, `mobi`, `chm` | All four |

### Examples

```bash
# Scan current directory, markdown to stdout
EbookScanner

# Scan a specific folder recursively
EbookScanner /Users/me/Books -r

# JSON output written to file
EbookScanner /Users/me/Books -r -f json -o catalog.json

# Scan only PDFs and EPUBs
EbookScanner /Users/me/Books --include pdf epub

# Scan top-level only (default), markdown output
EbookScanner /Users/me/Books
```

### Markdown Output

```markdown
# Ebook Catalog

**Directory:** /Users/me/Books
**Scanned:** 2026-03-24 08:00:00
**Total:** 2 books · PDF: 1 · EPUB: 1

---

## Clean Code

| Field | Value |
|-------|-------|
| Format | PDF |
| Authors | Robert C. Martin |
| Publisher | Prentice Hall |
| Pages | 431 |
| Published | 2008-08-01 |
| File | clean-code.pdf |
| Size | 4.0 MB |
```

### JSON Output

```json
{
  "scannedDirectory": "/Users/me/Books",
  "scannedAt": "2026-03-24T08:00:00+00:00",
  "books": [
    {
      "filePath": "/Users/me/Books/clean-code.pdf",
      "fileName": "clean-code.pdf",
      "format": "PDF",
      "fileSizeBytes": 4194304,
      "title": "Clean Code",
      "authors": ["Robert C. Martin"],
      "publisher": "Prentice Hall",
      "pageCount": 431
    }
  ]
}
```

Null fields are omitted from JSON output.

## MCP Subcommand

Starts an MCP server so AI assistants can invoke ebook scanning tools.

```bash
EbookScanner mcp [--http] [--port <n>]
```

| Option | Description | Default |
|--------|-------------|---------|
| `--http` | Use HTTP/SSE transport instead of stdio | stdio |
| `--port` | Port for HTTP transport | 3001 |

### Stdio transport (default)

```bash
EbookScanner mcp
```

Use this with MCP clients that communicate over stdin/stdout (e.g., Claude Desktop).

**Claude Desktop config** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "ebookscanner": {
      "command": "EbookScanner",
      "args": ["mcp"]
    }
  }
}
```

### HTTP/SSE transport

```bash
EbookScanner mcp --http --port 3001
```

Server listens at `http://127.0.0.1:3001/mcp`.

## MCP Tools

### `scan_directory`

Scans a directory and returns a catalog of all found ebooks.

**Parameters:**

| Name | Type | Description | Default |
|------|------|-------------|---------|
| `path` | string | Directory path to scan | required |
| `format` | string | `"markdown"` or `"json"` | `"markdown"` |
| `recursive` | bool | Scan subdirectories | `false` |

### `extract_metadata`

Extracts metadata from a single PDF, EPUB, MOBI, or CHM file.

**Parameters:**

| Name | Type | Description | Default |
|------|------|-------------|---------|
| `filePath` | string | Path to the ebook file | required |
| `format` | string | `"markdown"` or `"json"` | `"json"` |

## Dependencies

```xml
<PackageReference Include="System.CommandLine" />        <!-- 2.0.5 -->
<PackageReference Include="ModelContextProtocol" />      <!-- 1.1.0 -->
<PackageReference Include="ModelContextProtocol.AspNetCore" /> <!-- 1.1.0 -->
```

## Native AOT

The CLI is published with `<PublishAot>true</PublishAot>`, producing a single self-contained native binary. No .NET runtime needs to be installed on the target machine.

```bash
dotnet publish EbookScanner.Cli -r win-x64  -c Release   # → ~32 MB .exe
dotnet publish EbookScanner.Cli -r linux-x64 -c Release
dotnet publish EbookScanner.Cli -r osx-arm64 -c Release
```

All JSON serialization uses the **source-generated** `EbookScannerJsonContext` from `EbookScanner.Core` — no runtime reflection or dynamic code generation.
