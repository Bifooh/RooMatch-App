# Roomatch: manual assignment workspace (milestone 1 spec and checklist)

**Status:** draft for team review. Nothing here is implemented yet. Labels follow
[assignment-algorithm.md](assignment-algorithm.md#how-to-read-the-labels): **CONFIRMED PRODUCT DECISION**,
**PROPOSED TECHNICAL DEFAULT**, **QUESTION FOR LODGE CONFIRMATION**. Fictional data only (AC1).

## 1. Milestones

| # | Milestone | Label |
|---|---|---|
| 1 | Manual assignment workspace with backend validation (this document). | CONFIRMED PRODUCT DECISION |
| 2 | Before/after history, undo, optional structured reasons. | CONFIRMED PRODUCT DECISION |
| 3 | Automatic draft generation with explicit constraints and points ([assignment-algorithm.md](assignment-algorithm.md)). | CONFIRMED PRODUCT DECISION |
| Later | What-if alternatives; manager-approved preference adjustments. | CONFIRMED PRODUCT DECISION |

No cloud AI, automatic learning or live multi-user collaboration in the MVP (CONFIRMED PRODUCT DECISION).

## 2. Screens (CONFIRMED PRODUCT DECISION: board, unassigned list, conflict queue)

| Area | Contents | Layout details (PROPOSED TECHNICAL DEFAULT) |
|---|---|---|
| **Apartment board/table** | One row or card per apartment: number, plan, building, floor, effective capacity, occupants (names and tenant codes), lock icons, conflict badges. Apartments only; tenants choose bedrooms themselves. | Filter by building, floor, plan; "has space" and "has conflict" toggles; a table view for keyboard use. |
| **Unassigned list** | Contracted tenants with no apartment in the current draft, with their plan and open requests. | Grouped by plan; shows how many slots the plan still has. |
| **Conflict queue** | Validation problems and review items, kept uncluttered: one line per problem, grouped by type, newest changes first, with a link to the apartment or tenant. | Errors above warnings; dismissing a warning needs no reason (reasons never block). |
| **Tenant panel** | Plan, renewal status, preferences, roommate requests with **original wording, status, and the reason any request was excluded or left unresolved** (CONFIRMED PRODUCT DECISION). | Confidential restriction reasons are never shown; only "staff restriction". |

## 3. Editing and validation

| Rule | Label |
|---|---|
| The backend validates every move and swap; the UI never decides validity alone. | CONFIRMED PRODUCT DECISION (backend validation) |
| A swap is validated as a whole and saved atomically (one database transaction): both tenants move or neither does. | CONFIRMED PRODUCT DECISION |
| Every committed move or swap records all affected tenants' before and after apartments. | CONFIRMED PRODUCT DECISION (built in milestone 2; milestone 1 routes all writes through one service so history can be added there) |
| **Errors** (block the commit): wrong plan or building (H1), over capacity using `v_apartment_capacity` (H2, headcount until room types exist), required/excluded apartment or floor (H3), must-not-share (H4), moving a locked tenant (H7). | PROPOSED TECHNICAL DEFAULT |
| **Warnings** (allowed, listed in the queue): self-reported allergy with an animal holder, accessible apartment used by a non-needing tenant, renewal not in their current apartment, a roommate request broken by the move. | PROPOSED TECHNICAL DEFAULT |
| Whether staff may knowingly commit a confirmed-allergy conflict or other hard-rule break as an exception, and who may. | QUESTION FOR LODGE CONFIRMATION |
| Only one person edits at a time (no live collaboration). If two browser tabs edit the same draft, the second save fails with "draft changed, reload" (optimistic check on `updated_at`). | PROPOSED TECHNICAL DEFAULT |

## 4. Drafts, history, undo and reasons (milestone 2)

| Rule | Label |
|---|---|
| Saved **draft snapshots** plus a **change history**; no full event sourcing. | CONFIRMED PRODUCT DECISION |
| The **original generated draft** is kept unchanged as the evaluation baseline; staff edit a copy. | CONFIRMED PRODUCT DECISION |
| A change record: draft, a change group (one per move or swap), each affected tenant's before and after apartment, who, when, optional reason category, optional note, and whether it was later undone. | PROPOSED TECHNICAL DEFAULT |
| Reasons are optional and never block editing. Categories: **New information**; **Better tradeoff using existing information**; **One-time exception**; **No reason supplied** (the default). | CONFIRMED PRODUCT DECISION |
| Undo reverses a whole change group and is itself recorded. Undone edits are **not** treated as accepted preferences. | CONFIRMED PRODUCT DECISION |
| Unexplained edits are not reliable evidence for adjusting weights. | CONFIRMED PRODUCT DECISION |
| Notes stay local, visible only to authorized property staff, never to developers. | CONFIRMED PRODUCT DECISION |
| How long notes and history are kept. | QUESTION FOR LODGE CONFIRMATION (see [data-privacy.md](data-privacy.md#7-retention)) |

## 5. Schema mapping and gaps

What exists in `db/schema.sql` today:

| Need | Existing schema |
|---|---|
| Apartments, plan, building, floor, capacity | `apartments`, `floor_plans`, view `v_apartment_capacity` |
| Contracted tenants for the term | `contracts` (+ `tenants` for names) |
| A draft and its assignments | `assignment_drafts` (`source`, `status`), `assignments` (unique per draft and contract) |
| Locks | `assignments.is_locked`, `locked_by`, `locked_at` |
| Restrictions | `placement_restrictions` |
| Requests with original wording and exclusion reason | `roommate_requests` (`raw_text`, `status`, `exclusion_reason`) |
| Conflict and review items | `review_queue` (`item_type`, `original_wording`, `reason`, `status`) |
| Unassigned tenants | Derived: contracts of the term with no row in `assignments` for the draft |

Gaps, listed as **PROPOSED TECHNICAL DEFAULT, NOT applied**:

| Proposed change | Why |
|---|---|
| `assignment_changes` (change_id, draft_id, change_group_id, contract_id, from_apartment_id, to_apartment_id, changed_by, changed_at, reason_category ENUM('new_information','better_tradeoff_existing_info','one_time_exception','none'), note, undone_by_group_id) | Before/after history, reasons, undo (milestone 2). |
| `assignment_drafts.baseline_draft_id` and `is_baseline` (or a `parent_draft_id`) | Keep the generated draft as the baseline and edit a copy. |
| `assignment_drafts.algorithm_version`, `config_version`, `input_hash` | Reproducibility ([assignment-algorithm.md](assignment-algorithm.md#10-tie-breaking-and-reproducibility-proposed-technical-default-ap15)). |
| `staff_users` (or OS-account mapping) with a role | Changed-by, and notes visible only to authorized staff. |
| `review_queue` item types for live validation results (e.g. `capacity_exceeded`, `plan_mismatch`, `locked_move`) | Today's types cover data problems, not edit-time errors. Alternatively validation results stay computed, not stored. |
| Columns in assignment-algorithm.md section 16 (room type, wards, accessibility, allergy confirmation) | Needed for full validation; milestone 1 uses today's columns and shows the rest as not yet available. |

## 6. Milestone 1 checklist and acceptance criteria

All checks run on the fictional seeds (`lodge_small`, then `lodge_large`).

- [ ] **Load a draft.** Open draft 1 of `lodge_small`. *Accept:* 16 apartments, 32 contracts; the 2 locked tenants
  (E12 in 5793, E13 in 5955) show locks; the other 30 tenants appear in the unassigned list.
- [ ] **Board.** *Accept:* each apartment shows plan, floor, occupants and effective capacity; apartment 1375 shows 5
  (E16 override), not 6.
- [ ] **Move a tenant** (unassigned to an apartment, or apartment to apartment). *Accept:* saved only after backend
  validation; the board and unassigned list update without a page reload.
- [ ] **Swap two tenants.** *Accept:* validated as one operation and saved in one transaction; if either side is
  invalid, nothing changes.
- [ ] **Errors block.** *Accept:* the backend rejects, with a plain-language reason: a W-STD-3 tenant into an M-STD-3
  apartment (H1); a 7th tenant into a 6-capacity apartment and a 6th into 1375 (H2); FAKE-00026 and FAKE-00027 into
  the same apartment (H4, E14); moving the E12 locked tenant FAKE-00024 out of 5793 (H7); and, after a manager unlocks him, moving FAKE-00024 off floor 1 (H3).
- [ ] **Warnings allow.** *Accept:* putting FAKE-00006 and FAKE-00007 together (E02) is saved and listed as a warning
  in the conflict queue.
- [ ] **Validation lives in the backend.** *Accept:* the same rules reject a hand-crafted API request that bypasses
  the UI.
- [ ] **Requests preserved.** *Accept:* the tenant panel shows E03, E04, E06, E07, E08 and E14 requests with original
  wording and the reason each is unresolved or excluded.
- [ ] **Conflict queue stays uncluttered.** *Accept:* one line per problem, grouped by type; fixing a problem removes
  its line.
- [ ] **Oversubscription visible.** *Accept:* M-OTH-4 shows 10 contracts for 8 places; once 8 are assigned to 7215, a 9th is rejected (H2) and the shortfall is shown.
- [ ] **Scale.** *Accept:* `lodge_large` (1,100 tenants, 202 apartments) loads and validates a move in a time the
  team finds acceptable (to be measured, not promised).
- [ ] **Local only.** *Accept:* no external network calls from the frontend or API; no telemetry; API on localhost.
- [ ] **No personal data in logs.** *Accept:* logs show tenant codes and IDs only.
- [ ] **Tests.** *Accept:* backend validation has automated tests for each error and warning above.

Out of scope for milestone 1: history, undo, reasons (milestone 2); automatic drafts (milestone 3); login and roles
(required before any real data, see [data-privacy.md](data-privacy.md#5-who-can-see-what)).
