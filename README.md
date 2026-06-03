# Experimental Playground

Welcome to the **Experimental Playground** — a collection of exploratory, prototype‑driven, and occasionally wild ideas.  
This repository serves as a sandbox for testing concepts, validating patterns, and iterating quickly without the constraints of production‑grade polish.

The goal is simple: **learn fast, break things safely, and document insights along the way.**

---

## 🎯 Purpose

This repo exists to:

- Explore alternative approaches to complex technical problems  
- Validate architectural patterns, algorithms, and workflows  
- Compare competing designs through hands‑on experimentation  
- Capture learnings that can influence future production systems  
- Provide a shared space for rapid prototyping and collaboration

Each experiment is intentionally isolated, self‑contained, and free to evolve independently.

---

## 📦 Projects

| Project | Description | Status |
|---|---|---|
| [`MarkItDown`](MarkItDown/README.md) | C#/.NET 10 port of Microsoft's [markitdown](https://github.com/microsoft/markitdown) — converts documents and URLs to Markdown with Native AOT support and an MCP server | Active |
| [`MarkItDown/MarkItDown.Cli`](MarkItDown/MarkItDown.Cli/README.md) | CLI + MCP server — self-contained Native AOT binary (~65 MB) | Active |
| [`EbookScanner`](EbookScanner/README.md) | .NET 10 solution for scanning a file system for PDF, EPUB, MOBI, and CHM files and extracting their metadata into a Markdown or JSON catalog; includes an MCP server | Active |
| [`TurboVector`](TurboVector/README.md) | C#/.NET 10 port of [turbovec](https://github.com/RyanCodrai/turbovec) — a RaBitQ/TurboQuant vector quantization library for high-dimensional approximate nearest-neighbour search with SIMD-accelerated scoring | Active |
| [`BackgroundWorkers`](BackgroundWorkers/README.md) | .NET 10 multithreaded request pool with priority-based channel queue, mediator-pattern dispatcher, Orleans 10 grain integration, Aspire 13 orchestration, and a Blazor Server dashboard | Active |

---

## ⚠️ Disclaimer

This repository contains **experimental** and **non‑production** code.  
Expect breaking changes, incomplete features, and evolving patterns.  
Use at your own discretion.

---

## 🧭 Final Notes

This repo is meant to be a playground — a place to try bold ideas, test assumptions, and learn through iteration.  
Feel free to explore, question, and build upon anything you find here.
