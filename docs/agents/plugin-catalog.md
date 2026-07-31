# Repository plugin catalog

This repository contains a local Codex plugin marketplace at `.agents/plugins/marketplace.json`. The first plugin, `banking-core-engineering`, packages the `govern-banking-core` workflow.

## Layout

```text
.agents/plugins/
├── marketplace.json
└── plugins/
    └── banking-core-engineering/
        ├── .codex-plugin/plugin.json
        └── skills/govern-banking-core/
            ├── SKILL.md
            └── agents/openai.yaml
```

The catalog is intentionally small. Add a skill only for a focused repeatable workflow; add a plugin when skills or connectors need installable distribution. Keep detailed project truth in `docs/` and enforce deterministic rules with tests/constraints/CI rather than duplicating it in prompts.

## Install in a Codex environment

From a Codex CLI version that supports plugins:

```text
codex plugin marketplace add ./.agents/plugins
codex plugin add banking-core-engineering@personal
```

Then start a new thread so the installed skill is discovered. The current foundation environment does not include a `codex` executable, so installation was not performed here; the manifests were validated with the official scaffold validators.

## Authoring rules

- Scaffold/update through the maintained plugin/skill creator workflows.
- Keep skill frontmatter limited to `name` and trigger-focused `description`.
- Keep `SKILL.md` concise and imperative; route to root documents.
- Add scripts only for repeated deterministic operations, and test them.
- Validate each skill and plugin before commit.
- Treat marketplace entries as ordered distribution metadata and keep source paths relative to the marketplace root.
- Increment the plugin version for releases; use the supported cachebuster/reinstall flow only during local plugin iteration.

## Candidate future plugins

Do not scaffold these until repeated work proves the need:

- ledger conformance and invariant review;
- threat model/control-evidence review;
- payment-rail adapter certification;
- release/recovery evidence collection.

External connectors such as GitHub, issue tracking, or compliance evidence systems should use authorized MCP/apps rather than embedding credentials or live data access in a skill.
