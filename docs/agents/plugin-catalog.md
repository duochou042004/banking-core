# Repository plugin catalog

This repository exposes `govern-banking-core` in two supported forms: a repo-scoped skill under `.agents/skills/` for automatic local discovery, and a local Codex marketplace at `.agents/plugins/marketplace.json` for installable distribution. The `banking-core-engineering` plugin packages the same workflow for use outside this repository.

## Layout

```text
.agents/
├── plugins/marketplace.json
└── skills/govern-banking-core/
    ├── SKILL.md
    └── agents/openai.yaml

plugins/banking-core-engineering/
├── .codex-plugin/plugin.json
└── skills/govern-banking-core/
    ├── SKILL.md
    └── agents/openai.yaml
```

The catalog is intentionally small. Add a skill only for a focused repeatable workflow; add a plugin when skills or connectors need installable distribution. Keep detailed project truth in `docs/` and enforce deterministic rules with tests/constraints/CI rather than duplicating it in prompts.

Codex scans `$REPO_ROOT/.agents/skills`, so contributors working in this repository do not need to install the plugin. Keep the repo-scoped and packaged copies byte-for-byte equivalent. Install the plugin only to test packaging or to reuse the workflow outside this repository; installing it while inside the repo may show two same-named skill entries because Codex does not merge duplicates.

## Install in a Codex environment

From a Codex CLI version that supports plugins, register this non-default repo marketplace and install the package:

```text
codex plugin marketplace add .
codex plugin add banking-core-engineering@banking-core
codex plugin list --json
```

Then start a new CLI session so the installed skill is discovered. Plugins are supported in Codex CLI and the Codex/Work plugin surfaces, but not in the IDE extension; the repo-scoped skill remains available to Codex CLI and the IDE extension without plugin installation. Do not hard-code a machine-specific Codex executable path into project automation.

## Authoring rules

- Scaffold/update through the maintained plugin/skill creator workflows.
- Keep skill frontmatter limited to `name` and trigger-focused `description`.
- Keep `SKILL.md` concise and imperative; route to root documents.
- Add scripts only for repeated deterministic operations, and test them.
- Validate each skill and plugin before commit.
- Compare the two `govern-banking-core` skill folders recursively; a difference is a release-blocking packaging drift.
- Treat marketplace entries as ordered distribution metadata and keep source paths relative to the marketplace root.
- Increment the plugin version for releases; use the supported cachebuster/reinstall flow only during local plugin iteration.

## Candidate future plugins

Do not scaffold these until repeated work proves the need:

- ledger conformance and invariant review;
- threat model/control-evidence review;
- payment-rail adapter certification;
- release/recovery evidence collection.

External connectors such as GitHub, issue tracking, or compliance evidence systems should use authorized MCP/apps rather than embedding credentials or live data access in a skill.
