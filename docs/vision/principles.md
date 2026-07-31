# Engineering principles

1. **The ledger is a constitution, not a CRUD module.** Its rules are small, explicit, enforced at more than one layer, and changed rarely.
2. **Acknowledge only what can be recovered.** A successful response must correspond to durable, traceable state under the declared failure model.
3. **Prefer boring correctness.** Adopt mature primitives on critical paths; isolate experiments behind replaceable contracts.
4. **Start cohesive, distribute deliberately.** Process boundaries are costs. Begin modular and extract only with evidence.
5. **Make invalid states difficult to represent and impossible to post.** Database constraints, domain types, authorization, and tests reinforce each other.
6. **Design for retries and reconciliation.** Networks duplicate, reorder, delay, and lose messages. Correctness cannot depend on the happy path.
7. **Separate facts from projections.** Immutable financial facts are authoritative; balances, statements, search views, and analytics are rebuildable projections with declared consistency.
8. **Record decisions and provenance.** A material outcome must answer who, what, why, when, under which authority, and from which causal request.
9. **Minimize and compartmentalize sensitive data.** Tokenize, encrypt, redact, isolate, retain only for purpose, and make deletion obligations compatible with immutable financial records through separation and pseudonymization.
10. **Treat time as domain data.** Processing time, booking time, effective time, value date, and business date are different concepts.
11. **Compatibility is a product feature.** APIs, events, schemas, and migrations have versioning, deprecation, and rollback/roll-forward policies.
12. **Evidence beats adjectives.** “Secure,” “compliant,” “high availability,” and “exactly once” are prohibited shorthand without scope, model, and proof.
13. **Open by construction.** Mandatory capabilities use open source or open standards with viable open implementations and exit paths.
14. **Human accountability remains.** AI can accelerate research, implementation, and review but cannot be the sole approver of protected changes or legal/accounting conclusions.
