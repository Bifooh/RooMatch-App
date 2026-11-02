# Auto-match V1 (bounded first cut)

**Status:** implemented as a thin greedy placer. Not the full design in `assignment-algorithm.md`.

## What it does

1. Creates a **new working draft** for a term (optional copy of an existing draft + locks).
2. Builds a deterministic placement plan:
   - Prefer **mutual `resolved` roommate requests** (both sides resolved to each other’s tenant id).
   - Then **soft one-way** resolved preferences (join the requested person when same floor plan / capacity allows).
   - Soft-ignore `pending` / `unresolved` / `ambiguous` / `excluded` (no invented identities).
   - Greedy fill remaining unassigned contracts into apartments of the **same floor plan** with free **explicit capacity**.
3. Executes each placement through `AssignmentService.MoveAsync` (`source=generated`) so **history rows are written**.
4. Leaves people unassigned when capacity / validator blocks; returns skips + a compact `summary`.

## What it does not do

- Full weight / tier optimization, CP-SAT, what-if, AI.
- Bedroom-slot / private-room capacity, wards, accessibility, staff-confirmed allergy hard rules.
- Auto-resolving misspelled roommate names.
- Changing contracts or unlocking manager locks.
- Roommate-request prep UI (separate branch); this matcher only consumes already-`resolved` rows.

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

- `400` for missing body / missing `performedBy` / bad name / unknown term.
- `409` for draft/copy conflicts or when creation fails for other validation reasons.
- Response includes `summary.headline` plus `placedCount`, `stillUnassigned`, `mutualPairsPlaced`, `oneWayJoinsPlaced`, `placements`, `skips`, `notes`.

UI: **Generate draft (v1 match)** disables while running (“Generating draft…”), then shows a lasting summary banner.

## Contradictions vs `docs/assignment-algorithm.md` (proposals)

| Algorithm proposal | V1 reality |
|---|---|
| Bedroom-slot capacity (A2) | Uses `effective_capacity` headcount only (confirmed schema). |
| Tiered preference scoring (A6–A8) | Not implemented; greedy + mutual + soft one-way only. |
| Staff-confirmed allergy hard rule (A4) | Validator still warns only on legacy flags. |
| Seed-then-improve / CP-SAT (A12) | Seed-only greedy; no improve pass. |
| Suggest fuzzy roommate matches (A9) | Matcher never resolves text; only uses already-`resolved` rows. |
| Explanations with points (A13) | History reason is a short phrase, not a score breakdown. |

Treat the algorithm doc as design input, not as a checklist V1 failed — V1 is intentionally smaller.

## Next lever

Land roommate-request prep → resolve mutual/one-way names → re-run match. Then group packing / mild scoring still via `MoveAsync`, still no full optimizer.
