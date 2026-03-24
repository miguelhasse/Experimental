# EbookScanner

A .NET 10 solution for scanning a file system directory tree for **PDF**, **EPUB**, **MOBI**, and **CHM** files and extracting their metadata into a structured catalog in **Markdown** or **JSON** format.

The solution also exposes an **MCP (Model Context Protocol) server** for AI assistant integration.

## Architecture

![architecture diagram](/assets/diagrams/ebookscanner.svg)

## Quick Start

```bash
# Scan current directory (top-level only, markdown output)
dotnet run --project EbookScanner.Cli

# Scan a library folder recursively, output JSON to file
dotnet run --project EbookScanner.Cli -- /path/to/library --recursive --format json --output catalog.json

# Start MCP server (stdio transport)
dotnet run --project EbookScanner.Cli -- mcp
```

## CLI Reference

```
USAGE:
  EbookScanner [<directory>] [options]
  EbookScanner mcp [options]

ARGUMENTS:
  <directory>    Directory to scan (default: current directory)

OPTIONS:
  -o, --output <file>        Write output to file instead of stdout
  -f, --format <format>      Output format: markdown or json  [default: markdown]
  -r, --recursive            Scan subdirectories recursively
      --include <fmt>...     Formats to include: pdf, epub, mobi, chm  [default: all]
  -?, -h, --help             Show help
      --version              Show version

MCP SUBCOMMAND:
  mcp [options]
  --http                     Use HTTP/SSE transport instead of stdio
  --port <n>                 TCP port for HTTP transport  [default: 3001]
```

### Examples

```bash
# Markdown catalog of a flat directory
EbookScanner /Users/me/Books

# JSON catalog, recursive, PDFs only
EbookScanner /Users/me/Books -f json -r --include pdf

# Write markdown catalog to a file
EbookScanner /Users/me/Books -o catalog.md -r

# MCP server over stdio (for use with Claude Desktop, etc.)
EbookScanner mcp

# MCP server over HTTP
EbookScanner mcp --http --port 3001
```

## Markdown Output Format

```markdown
# Ebook Catalog

**Directory:** /Users/me/Books
**Scanned:** 2026-03-24 08:00:00
**Total:** 4 books · PDF: 1 · EPUB: 1 · MOBI: 1 · CHM: 1

---

## Clean Code

| Field | Value |
|-------|-------|
| Format | PDF |
| Authors | Robert C. Martin |
| Publisher | Prentice Hall |
| Language | en |
| Pages | 431 |
| Published | 2008-08-01 |
| Tags | programming, software engineering |
| File | clean-code.pdf |
| Size | 4.0 MB |
```

## MCP Tools

When running as an MCP server, the following tools are exposed:

| Tool | Description |
|------|-------------|
| `scan_directory` | Scan a directory and return a metadata catalog |
| `extract_metadata` | Extract metadata from a single file |

### `scan_directory`

```json
{
  "path": "/path/to/library",
  "format": "markdown",
  "recursive": false
}
```

### `extract_metadata`

```json
{
  "filePath": "/path/to/book.epub",
  "format": "json"
}
```

## Supported Formats & Extracted Metadata

| Format | Title | Authors | Publisher | Language | ISBN | Pages | Dates | Tags |
|--------|-------|---------|-----------|----------|------|-------|-------|------|
| PDF    | ✓ | ✓ | ✓ (Creator) | — | — | ✓ | Creation + Modified | ✓ (Keywords) |
| EPUB   | ✓ | ✓ | ✓ | ✓ | ✓ | — | Published | ✓ (Subjects) |
| MOBI   | ✓ | ✓ | ✓ | ✓ | ✓ | — | Published | ✓ |
| CHM    | ✓ | — | — | ✓ (LCID) | — | — | — | — |

> MOBI and CHM metadata are extracted by parsing binary file formats directly — no third-party library required.

## Packages

| Package | Version | Used In |
|---------|---------|---------|
| `UglyToad.PdfPig` | 1.7.0-custom-5 | Core |
| `VersOne.Epub` | 3.3.6 | Core |
| `System.CommandLine` | 2.0.5 | Cli |
| `ModelContextProtocol` | 1.1.0 | Cli |
| `ModelContextProtocol.AspNetCore` | 1.1.0 | Cli |
| `xunit.v3` | 3.2.2 | Tests |

## Building & Testing

```bash
# Build the full solution
dotnet build EbookScanner.slnx

# Run all unit tests (60 tests)
dotnet test EbookScanner.slnx
```

## Native AOT Publishing

The CLI project supports [Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/) compilation (`PublishAot=true`). This produces a single self-contained binary with no .NET runtime dependency and fast startup time.

```bash
# Build a native binary for the current platform
dotnet publish EbookScanner.Cli -r win-x64 -c Release
dotnet publish EbookScanner.Cli -r linux-x64 -c Release
dotnet publish EbookScanner.Cli -r osx-arm64 -c Release
```

The output is placed in `EbookScanner.Cli/bin/Release/net10.0/<rid>/publish/`. The native binary is approximately 32 MB on Windows and is fully functional — no JIT warm-up, no runtime install required.

JSON serialization uses **source-generated** `JsonSerializerContext` (`EbookScannerJsonContext`) in place of reflection, ensuring trim-safe output.

Requires .NET SDK 10.0.201 or later.
