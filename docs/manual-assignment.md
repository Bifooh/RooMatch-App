# Manual assignment workspace (implemented)

**Status:** manual workspace, history/undo, roommate prep, and auto-match v1 are implemented. Full weighted matching is still deferred.

## What staff can do

1. Select a term and an existing draft.
2. Create a new working draft (empty or copy of another draft’s assignments/locks). The source draft is unchanged.
3. See active apartments (number, plan, floor/building, occupants, occupancy / effective capacity).
4. See eligible unassigned tenants for the draft’s term (every term contract appears exactly once: assigned or unassigned).
5. Search/filter by tenant name or code, apartment number, or floor plan.
6. Move a selected tenant to another apartment or unassign them.
7. Swap two assigned tenants atomically.
8. Lock/unlock an assignment with an explicit fictional operator identity (no auth).
9. Review a compact validation panel (details on demand), plus original roommate-request wording.
10. Edits persist in MySQL across refresh.
11. Review recent change history for the selected draft; undo last or undo a specific row (optional reason).
12. Prep roommate-request conflicts for the selected **term** (resolve duplicates, mark missing/ambiguous/excluded) before matching — see `docs/roommate-request-prep.md`.

## Validation (backend authoritative)

Reusable C# service: `Lodge.Core.Validation.AssignmentValidator`, called by `Lodge.Core.Services.AssignmentService`
(central mutation path). Successful move / unassign / swap / lock / unlock rows are recorded for undo
(see **History & undo** below). Matching can call the same validator later.

| Code | Severity | Meaning |
|---|---|---|
| `DRAFT_NOT_EDITABLE` | error | Draft status is `final` or `archived` |
| `CONTRACT_NOT_FOUND` / `CONTRACT_TERM_MISMATCH` | error | Contract missing or wrong term |
| `APARTMENT_NOT_FOUND` / `APARTMENT_INACTIVE` | error | Destination missing or inactive |
| `FLOOR_PLAN_MISMATCH` | error | Contracted plan ≠ apartment plan |
| `CAPACITY_EXCEEDED` | error | Occupancy would exceed `v_apartment_capacity.effective_capacity` |
| `ASSIGNMENT_LOCKED` | error | Manager lock blocks the change |
| `RESTRICTION_*` | error | Staff-confirmed `placement_restrictions` violated |
| `SWAP_*` / `LOCK_OWNER_REQUIRED` / `NOT_ASSIGNED` | error | Swap/lock preconditions |
| `UNCONFIRMED_ALLERGY_COLOCATION` | **warning only** | Legacy `has_animal` / `animal_allergy_restriction` flags coincide — **not enforced** |
| `UNRESOLVED_ROOMMATE_REQUEST` | info | Pending/unresolved/ambiguous/excluded wording for review |

Moves and swaps validate the **resulting** state (both apartments for a swap). Rejected ops leave the DB unchanged (single transaction).

## Confirmed vs proposal (do not treat proposals as policy)

**Enforced (schema / Samuel-confirmed capacity rules):**

- Assignments are apartment numbers only; capacity from `floor_plans.resident_capacity` or `apartments.capacity_override`.
- Contracted floor plan must match the apartment.
- One assignment per contract per draft; draft and contract share a term.
- Manager locks require `locked_by`.
- Staff-confirmed `placement_restrictions` rows.

**Not enforced (proposals / missing schema — see `docs/assignment-algorithm.md` §14):**

- Private-room bedroom-slot capacity (`contracts.room_type` not in schema).
- Ward mapping (`apartments.ward_id` not in schema).
- Accessibility need / adapted apartments.
- Staff-confirmed allergy hard rule (no `allergy_confirmed_by` fields; legacy preference booleans are warnings only).
- Automatic roommate-request matching (prep UI can resolve/mark; seeds start `pending`; `raw_text` never overwritten).

This workspace never claims full validation against unimplemented proposal rules.

## API

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/terms` | List terms |
| GET | `/api/terms/{termId}/drafts` | List drafts |
| POST | `/api/terms/{termId}/drafts` | Create empty or copy draft |
| GET | `/api/drafts/{draftId}/workspace` | Full workspace payload |
| POST | `/api/drafts/{draftId}/move` | Assign / reassign / unassign |
| POST | `/api/drafts/{draftId}/swap` | Atomic swap |
| POST | `/api/drafts/{draftId}/lock` | Lock / unlock |
| GET | `/api/drafts/{draftId}/history` | Recent change history (`?limit=50`) |
| POST | `/api/drafts/{draftId}/undo` | Undo last undoable change, or `{ historyId }` |
| GET | `/api/terms/{termId}/roommate-requests/prep` | Roommate conflict/prep list for the term |
| POST | `/api/terms/{termId}/roommate-requests/{requestId}/resolve` | Confirm resolved tenant |
| POST | `/api/terms/{termId}/roommate-requests/{requestId}/mark` | Mark unresolved / ambiguous / excluded |
| POST | `/api/terms/{termId}/match` | V1 auto-match → new draft (see `docs/auto-match-v1.md`) |

Move/swap accept optional `performedBy` + `reason` (reason never required). Lock uses `lockedBy` as the operator identity. Roommate prep uses the same fictional operator.

## History & undo

**Implemented**

- Table `assignment_change_history` (incremental migration `db/migrations/001_assignment_change_history.sql`).
- Before/after assignment snapshots (contract id, tenant code, apartment id/number, lock fields) for successful mutations.
- List history newest-first; `isUndoable` when not yet undone and not itself an undo row.
- Undo restores `before_state`, re-checks blocking draft validation, writes an `undo` history row, marks the original `undone_by_history_id`. Transactional; conflicts / validation failures leave the DB unchanged.
- Draft-copy is **not** recorded as undoable history.
- Workspace UI: history panel, Undo last / per-row Undo, optional reason field (never required).

**Deferred**

- Structured reason picker / learning from unexplained edits, full event sourcing, undoing draft-copy, multi-user auth.

### Apply migration

```bash
# after schema + seed are loaded
mysql lodge_small < db/migrations/001_assignment_change_history.sql
# Mac user-space example:
# /usr/local/mysql/bin/mysql -uroot --socket="$HOME/mysql-lodge/run/mysql.sock" lodge_small \
#   < db/migrations/001_assignment_change_history.sql
```

Safe to re-run (`CREATE TABLE IF NOT EXISTS`). Does not wipe seed data.

## Deferred (out of scope here)

Auto matching, AI, what-if scenarios, real import, multi-user auth/collab, cloud deploy, full event sourcing, draft-copy undo.
