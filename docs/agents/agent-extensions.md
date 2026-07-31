# Agent extensions and distribution

The repository supports Codex and Claude Code without making either agent the source of project truth. `AGENTS.md`, the documents under `docs/`, and the canonical workflow inside `plugins/banking-core-engineering` are authoritative. Host-specific files only make that guidance discoverable or installable.

## Why the directories are separate

| Path | Consumer | Responsibility |
| --- | --- | --- |
| `AGENTS.md` | Codex and other compatible agents | Shared persistent rules and routing. |
| `CLAUDE.md` | Claude Code | Imports `AGENTS.md` and adds only Claude-specific adaptation. |
| `.agents/skills/govern-banking-core/` | Codex | Thin repo-skill adapter for automatic project discovery. |
| `.claude/skills/govern-banking-core/` | Claude Code | Thin repo-skill adapter for automatic project discovery. |
| `.agents/plugins/marketplace.json` | Codex | Repo marketplace catalog; it does not contain the plugin payload. |
| `.claude-plugin/marketplace.json` | Claude Code | Repo marketplace catalog; it does not contain the plugin payload. |
| `plugins/banking-core-engineering/` | Both hosts | Canonical, self-contained distribution package and workflow. |

Both marketplace formats resolve `./plugins/banking-core-engineering` from the repository root and copy/cache the package when installed. That is why `plugins/` is outside `.agents/` and `.claude-plugin/`. A nested `.agents/plugins/plugins/` package is incorrect and must not be recreated.

## Layout

```text
AGENTS.md
CLAUDE.md
.agents/
├── plugins/marketplace.json
└── skills/govern-banking-core/
    ├── SKILL.md
    └── agents/openai.yaml
.claude/
└── skills/govern-banking-core/SKILL.md
.claude-plugin/marketplace.json
plugins/banking-core-engineering/
├── .codex-plugin/plugin.json
├── .claude-plugin/plugin.json
└── skills/govern-banking-core/
    ├── SKILL.md
    └── agents/openai.yaml
```

The full workflow lives only in `plugins/banking-core-engineering/skills/govern-banking-core/SKILL.md`. The two project-skill adapters carry discovery metadata and instruct the active agent to load that canonical file. This avoids policy drift without relying on symlinks that are awkward on Windows.

## Work inside this repository

No plugin installation is required:

- Codex discovers the repo adapter and invokes it as `$govern-banking-core`.
- Claude Code loads `CLAUDE.md`, discovers the repo adapter, and invokes it as `/govern-banking-core`.

Installing the distribution plugin while working in this repository is unnecessary and can expose the same workflow twice. Use the plugin package for validation or for reuse outside this checkout.

## Install the distribution package

Codex CLI:

```text
codex plugin marketplace add .
codex plugin add banking-core-engineering@banking-core
codex plugin list --json
```

Claude Code:

```text
claude plugin marketplace add .
claude plugin install banking-core-engineering@banking-core
claude plugin list --json
```

Start a new session or reload plugins after installation. Claude namespaces installed plugin skills, so its installed invocation is `/banking-core-engineering:govern-banking-core`; its repo-local invocation remains `/govern-banking-core`.

## Authoring and validation rules

- Keep `AGENTS.md` provider-neutral; put host-only behavior in `CLAUDE.md` or host metadata.
- Keep the complete workflow only in the packaged skill. Host adapters must remain short and must point to it.
- Keep the adapter names and descriptions equal to the canonical skill so implicit discovery remains consistent.
- Keep the Codex and Claude plugin manifest versions in lockstep for a shared package release.
- Keep plugin component paths within `plugins/banking-core-engineering`; installed plugins are cached and cannot rely on arbitrary repository-relative files.
- Add hooks, MCP servers, permissions, subagents, or scripts only for a demonstrated workflow and review their trust boundary before enabling them.
- Validate both project adapters, the canonical skill, both manifests, both marketplaces, relative Markdown links, and packaging paths before commit.

Project-wide Claude settings, hooks, MCP servers, and custom subagents are intentionally absent. Phase 0 has no executable build workflow or external-system requirement that justifies those capabilities yet.
