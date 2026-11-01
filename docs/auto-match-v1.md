# Auto-match V1 (bounded first cut)

**Status:** implemented as a thin greedy placer. Not the full design in `assignment-algorithm.md`.

## What it does

1. Creates a **new working draft** for a term (optional copy of an existing draft + locks).
2. Builds a deterministic placement plan:
   - Prefer **mutual `resolved` roommate requests** (both sides resolved to each other’s tenant id).
   - Soft-ignore `pending` / `unresolved` / `ambiguous` / `excluded` (no invented identities).
   - Greedy fill remaining unassigned contracts into apartments of the **same floor plan** with free **explicit capacity**.
3. Executes each placement through `AssignmentService.MoveAsync` (`source=generated`) so **history rows are written**.
4. Leaves people unassigned when capacity / validator blocks; returns skips.

## What it does not do

- Full weight / tier optimization, CP-SAT, what-if, AI.
- Bedroom-slot / private-room capacity, wards, accessibility, staff-confirmed allergy hard rules.
- Auto-resolving misspelled roommate names.
- Changing contracts or unlocking manager locks.

## API

```http
POST /api/terms/{termId}/match
Content-Type: application/json

{
  "draftName": null,
  "copyFromDraftId": null,
  "copyLocks": true,
  "performedBy": "Fictional Matcher",
  "notes": "auto-match v1"
}
```

Response includes `draft`, `placedCount`, `stillUnassigned`, `mutualPairsPlaced`, `placements`, `skips`, `notes`.

UI: **Generate draft (v1 match)** copies the currently selected draft (if any) then places.

## Contradictions vs `docs/assignment-algorithm.md` (proposals)

| Algorithm proposal | V1 reality |
|---|---|
| Bedroom-slot capacity (A2) | Uses `effective_capacity` headcount only (confirmed schema). |
| Tiered preference scoring (A6–A8) | Not implemented; greedy + mutual pairs only. |
| Staff-confirmed allergy hard rule (A4) | Validator still warns only on legacy flags. |
| Seed-then-improve / CP-SAT (A12) | Seed-only greedy; no improve pass. |
| Suggest fuzzy roommate matches (A9) | Matcher never resolves text; only uses already-`resolved` rows. |
| Explanations with points (A13) | History reason is a short phrase, not a score breakdown. |

Treat the algorithm doc as design input, not as a checklist V1 failed — V1 is intentionally smaller.

## Next lever

After roommate-request prep lands: resolve mutual names first, then re-run match. Next matcher step: pack groups / one-way requests softly, still via `MoveAsync`, still no full optimizer.
