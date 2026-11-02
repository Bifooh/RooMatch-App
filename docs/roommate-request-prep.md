# Roommate request prep / conflict UI (implemented)

**Status:** term-scoped prep panel + APIs are implemented. Auto-matching is still deferred.

## Purpose

Show roommate-request conflicts **before** matching so managers can fix what is fixable. If not fixable, mark unresolved / ambiguous / excluded so a later matcher may ignore or use soft similarity. This milestone does **not** place anyone.

## Confirmed vs proposal

**Confirmed (schema / this UI):**

- One `roommate_requests` row per named person; `raw_text` is never overwritten.
- Statuses: `pending`, `resolved`, `unresolved`, `ambiguous`, `excluded` (reason required for the last three).
- Stable IDs (`request_id`, `contract_id`, `tenant_id`).
- `review_queue` rows opened/updated when marking; closed when resolving.
- Seeds stay mostly `pending` until a manager acts.

**Proposal / deferred (algorithm doc):**

- Auto-matching and soft similarity scoring.
- Aggressive auto-guess from misspellings (explicitly **not** done here).
- Real auth (fictional operator identity only).

## Matching hints used for prep only

| Conflict | Meaning | Manager action |
|---|---|---|
| `exact` | One exact full-name (or unambiguous first-name) candidate | Confirm pick |
| `duplicate` | Several people match the wording | Pick one, or mark ambiguous/unresolved |
| `nearOnly` | Edit-distance hint(s) only — never auto-resolved | Pick only if known, else unresolved |
| `missing` | No candidates | Mark unresolved/excluded with reason; do not invent identity |

## API

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/terms/{termId}/roommate-requests/prep` | Attention list + counts + candidates |
| POST | `/api/terms/{termId}/roommate-requests/{requestId}/resolve` | `{ resolvedTenantId, performedBy, note? }` |
| POST | `/api/terms/{termId}/roommate-requests/{requestId}/mark` | `{ status, exclusionReason, performedBy, note? }` |

No new migration — uses existing `roommate_requests` and `review_queue`.

## UI

On the assignment workspace, with a term selected: **Roommate request prep** panel (filterable). Uses the same fictional operator field. Double-submit guarded via shared `busy` flag.

## Deferred

Auto-matching, AI, real auth, learning from unexplained edits, ward-request resolution.
