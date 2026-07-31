# Glossary

Use these terms consistently in specifications, APIs, events, code, and operations.

| Term | Meaning |
| --- | --- |
| Account | A domain container governed by an agreement and product. It may map to one or more ledger accounts but is not itself necessarily an accounting account. |
| Available balance | Amount currently usable after posted balance, holds, limits, and policy are applied. It is not interchangeable with posted balance. |
| Balance | A value derived from postings for a defined account, asset, balance type, and point in time. Cached balances are projections. |
| Booking time | When a transaction becomes an authoritative accounting fact in this system. |
| Business date | Institution-controlled accounting date, which may differ from the civil date and timezone. |
| Command | An authenticated request to change state. Commands may be rejected and must have defined idempotency behavior. |
| Effective time | When a domain fact is considered effective. It may be before or after booking time under policy. |
| Event | An immutable statement that a fact occurred. An integration event is not automatically the ledger source of truth. |
| General ledger | Institution-level accounting used for financial reporting. It receives controlled summaries or postings from operational subledgers. |
| Hold | A temporary reservation that reduces availability without creating a posted transfer of ownership. |
| Idempotency key | Caller-provided token scoped to an operation and tenant. Repeating the same request returns the original outcome; a different request with the same key is rejected. |
| Journal | One atomic accounting business event containing balanced postings and provenance. |
| Ledger | An isolated book with a chart/rules, ordered journals, and balances. A deployment may contain multiple ledgers. |
| Legal entity | A regulated or contractual organization whose books, data, and authority boundaries must be explicit. |
| Posting | One immutable debit or credit line in a journal against a ledger account and one asset. |
| Posted balance | Balance from booked postings; pending holds or instructions do not silently alter it. |
| Reconciliation | Comparison of independently derived records or positions, with explicit investigation and resolution of differences. |
| Reversal | A new journal that negates an earlier journal while preserving both and linking their provenance. |
| Settlement | Final discharge of an obligation on the relevant external or internal books; it is distinct from authorization and clearing. |
| Subledger | Detailed operational accounting that supports accounts/products and reconciles to the general ledger. |
| Tenant | A software isolation scope. It must not be assumed to equal a legal entity; the mapping is deployment policy. |
| Value date | Date from which economic effects such as interest apply, subject to product and accounting policy. |

When a rail, jurisdiction, or product uses a conflicting term, define a qualified term and map it explicitly instead of silently overloading this vocabulary.
