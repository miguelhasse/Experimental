# Project Context

- **Owner:** Marco Antonio Silva
- **Project:** Experimental — a monorepo hub for multiple projects/experiments
- **Stack:** Mixed (varies per sub-project)
- **Structure:** Each root folder is a separate project (currently: MarkItDown)
- **Created:** 2026-03-23

## Learnings

- **Fork PR Workflow**: This repo uses a cross-fork contribution pattern. Contributor (marconsilva) forks upstream, pushes branch to fork, then uses `gh pr create --head marconsilva:squad --base main` to create PR from fork to upstream. Useful when contributor lacks direct push access.
