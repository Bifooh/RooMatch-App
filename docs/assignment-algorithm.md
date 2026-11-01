# Roomatch: how tenants get assigned to apartments (DRAFT design)

**Status:** draft for team review. Nothing in this document is implemented yet. It describes *what* the assignment
engine should decide and *why*, in plain words, so it can be critiqued (including by other AI tools) before anyone
writes solver code. Inputs: Samuel's notes and the field lists (no values) of the Lodge's two student forms; see
[data-privacy.md](data-privacy.md). The manual workspace that comes first is specified in
[manual-workspace.md](manual-workspace.md).

All names and tenant codes in the examples are **fictional** (they come from the repo's generated test data).

## How to read the labels

Every decision in this document carries exactly one of three labels.

| Label | Meaning |
|---|---|
| **CONFIRMED PRODUCT DECISION** | Decided by Samuel's team. Build on it. |
| **PROPOSED TECHNICAL DEFAULT** | Our suggestion. Not approved. It needs the team's explicit approval, and may change after review. |
| **QUESTION FOR LODGE CONFIRMATION** | Only authorized Lodge staff can answer it (policy, practice, or data). Nothing is built on an assumed answer. |

Facts that Samuel reported about the Lodge (two buildings, 4 floors each, plan capacities, terms) are marked
*(Samuel/manager input)*. They are inputs, not decisions. Policies are never inferred from them, and nothing here is a
legal conclusion.

## 0. Decision register

### 0.1 CONFIRMED PRODUCT DECISION

| ID | Decision |
|---|---|
| AC1 | Lodge-only MVP, fictional data only, one computer. |
| AC2 | React/TypeScript frontend, ASP.NET Core/C# backend, MySQL. |
| AC3 | Tenants are assigned to **apartments only**. Tenants choose bedrooms themselves. |
| AC4 | Implementation order: (1) manual assignment workspace with backend validation; (2) before/after history, undo and optional structured reasons; (3) automatic draft generation using explicit constraints and points; later: what-if alternatives and manager-approved preference adjustments. |
| AC5 | No cloud AI, no automatic learning, no live multi-user collaboration in the MVP. |
| AC6 | Workspace: apartment board/table, unassigned list, uncluttered conflict queue; unresolved roommate requests are preserved with the reason they were excluded. ([manual-workspace.md](manual-workspace.md)) |
| AC7 | Every committed move or swap records all affected tenants' before/after assignments; swaps are validated and saved atomically. |
| AC8 | Saved draft snapshots plus change history (no full event sourcing). The original generated draft is preserved as the evaluation baseline. |
| AC9 | Reasons are optional and never block editing. Categories: New information; Better tradeoff using existing information; One-time exception; No reason supplied. |
| AC10 | Undo history is retained, but undone edits are not accepted preferences. Unexplained edits are not reliable evidence for adjusting weights. Notes stay local and are visible only to authorized property staff, not developers. |
| AC11 | Success measures are defined as in section 15; the ~90% acceptance and under-60-minute targets are hypotheses to validate, not promises. |
| AC12 | Apartment-level compatibility is the initial scoring proposal to work from; bedroom-pair scoring is an unvalidated alternative, not an approved improvement (5.6). |

### 0.2 PROPOSED TECHNICAL DEFAULT (needs team approval)

| ID | Proposal | Section |
|---|---|---|
| AP1 | The contracted floor plan (and so the building) is a hard rule; the engine never changes contracts. | 4 |
| AP2 | Capacity in bedroom slots (shared = 1, private = 2, apartment = 2 x bedrooms), plus any staff override. | 4.1 |
| AP3 | Staff restrictions, must-not-share and manager locks are hard rules. | 4 |
| AP4 | Animal/allergy handling: hard only after staff confirm documentation; a self-reported Yes is a strong avoidance (tier 0b) plus a review item. No automatic animal-holder locks until the Lodge confirms the policy. | 4.3 |
| AP5 | Gender is not stored; the contracted plan decides the building. | 4.2 |
| AP6 | Points per tenant (not per pair), proportional to how much is satisfied. | 5.1 |
| AP7 | The recommended weights in 5.1 (including cleanliness .75 and ward request .50). | 5.1 |
| AP8 | **No double counting:** renewal stay (S1) and two-semester continuity (S10) are one "continuity" line, earned at most once per tenant. | 5.1 |
| AP9 | Tiered (lexicographic) scoring, with the tier-1 tradeoff stated openly. | 5.4 |
| AP10 | Missing answers get a fixed "expected" credit (0.5) instead of being omitted, and data coverage is reported separately. | 5.5 |
| AP11 | Closeness scoring for ordered answers (same = 1, one step = 0.5, further = 0). | 5.1 |
| AP12 | Name resolution: exact match only with a **unique eligible target-term** tenant; everything else goes to review. Prefer the Lodge's resident ID for identity. | 6 |
| AP13 | Cross-building and different-plan requests are excluded and flagged, never forced. | 6 |
| AP14 | Build "seed then improve" first; CP-SAT later as a check. Report "search did not find a placement" separately from "proven impossible". | 9 |
| AP15 | Reproducibility: record input, configuration and algorithm versions (not just a seed). | 10 |
| AP16 | Explanations show points and the blocking rule, never a confidential reason. | 11 |
| AP17 | A later "improve existing draft" workflow that minimizes moves and presents tradeoffs. | 12 |
| AP18 | Waitlist and parking are out of scope. | 4.2 |

### 0.3 QUESTION FOR LODGE CONFIRMATION

The full list is in section 18. The most important ones: which renewal stays are **guaranteed** versus preferred;
whether two-semester signups are promised the same apartment; animal/allergy documentation, override and any
"don't move the animal holder" rule; whether accessible apartments must stay reserved; who is left unplaced when a
plan is oversubscribed.

---

## 1. Purpose and context

The Lodge is student housing with **two buildings, Women's and Men's, each with 4 floors** (Samuel/manager input).
Each term, every tenant has a signed contract, and staff give each contracted tenant an **apartment number**. Today
this is done by hand. Roomatch first gives staff a better manual workspace (AC4), then history and undo, and only
then an automatic *draft* that staff review, edit, lock and finalize.

- **The contract comes first.** Tenants sign before roommates are known. The contract fixes the **floor plan** and the
  **price**, and the floor plan fixes the building (Samuel/manager input).
- **Apartments only; tenants choose bedrooms themselves** (CONFIRMED PRODUCT DECISION, AC3).
- **Everyone is LDS.** The Lodge is one stake of about **6 wards, each with 150+ people**; a ward is tied to a set of
  apartments, so a tenant's ward is a *result* of placement (Samuel/manager input; section 8).
- The goal is a **good, explainable, repeatable** draft. Staff stay in charge.

## 2. Terms

The Lodge runs **Fall, Winter and Spring; there is no Summer term** (Samuel/manager input). Each term is assigned
separately. PROPOSED TECHNICAL DEFAULT: a **renewing** tenant lived at the Lodge in the previous term and has a
contract for the next one; their "current apartment" is where they lived that term. QUESTION FOR LODGE CONFIRMATION:
does a tenant returning in Fall after Spring count as a renewal for staying purposes?

## 3. The two-layer model

1. **Hard rules** (section 4): yes/no checks the engine never breaks. If they make a placement impossible, the
   tenant stays **unplaced with the reason**.
2. **A points score** (section 5): among plans that pass every hard rule, prefer more points. Every point earned or
   missed is listed, so each placement can be explained.

Points let the engine weigh conflicts consistently (two friends want the last spot; a renewal blocks a group). Hard
rules exist because some things (wrong building, over capacity) must never be "worth it".

## 4. Hard rules

All rows are **PROPOSED TECHNICAL DEFAULT** unless marked otherwise.

| # | Rule | Source | Notes |
|---|---|---|---|
| H1 | **Contracted floor plan.** Only apartments of the tenant's contracted plan (which fixes the building). | Samuel/manager input (the contract fixes plan and price) | The engine never changes a contract; a request needing another plan becomes a staff flag (E08, E11). |
| H2 | **Capacity, in bedroom slots** (4.1). | Samuel/manager input (2 per bedroom, private rooms) | Includes `capacity_override`. |
| H3 | **Staff placement restrictions**: required/excluded apartment or floor. | Existing schema | Entered only after staff confirm; the confidential reason is never stored. |
| H4 | **must_not_share_with.** | Existing schema | Beats any roommate request (E14). |
| H5 | **Animal vs confirmed allergy** (4.3). | Lodge form text (via Samuel) | Hard only once documentation is staff-confirmed. QUESTION FOR LODGE CONFIRMATION: the documentation rule and who may override. |
| H6 | **Accessibility need.** A tenant with a staff-confirmed need gets a handicap-adapted apartment of their plan. | Samuel/manager input: a few first-floor apartments are adapted | QUESTION FOR LODGE CONFIRMATION: who confirms a need; whether these apartments must stay reserved (S7). |
| H7 | **Manager locks** are kept exactly. | Existing schema | A lock that breaks another hard rule is shown as a conflict; the engine never moves it. |
| H8 | **One apartment per contract per draft**; nobody is moved to another plan to make room. | Existing schema | Unplaced tenants are reported separately (E15, section 15). |

The earlier draft had an "H9: approved animal holders are pinned in place" rule. It is **withdrawn as a rule** and
is now a QUESTION FOR LODGE CONFIRMATION (4.3): we do not infer it from the form text.

### 4.1 Capacity in bedroom slots

Every bedroom holds **2 people by default** (Samuel/manager input). Plan capacities (Samuel/manager input; all 10
confirmed by Samuel 2026-10-31):

| Plan | Bedrooms | Default headcount |
|---|---|---|
| Women's Standard 2bd/2ba (W-STD-2) | 2 | 4 |
| Women's Oversized 2bd/2ba (W-OVR-2) | 2 | 4 |
| Women's Standard 3bd/3ba (W-STD-3) | 3 | 6 |
| Women's Flat 4bd/4ba (W-FLAT-4) | 4 | 8 |
| Women's Standard Townhome 4bd/4ba (W-STH-4) | 4 | 8 |
| Women's Oversized Townhome 4bd/4ba (W-OTH-4) | 4 | 8 |
| Men's Standard 3bd/3ba (M-STD-3) | 3 | 6 |
| Men's Flat 4bd/4ba (M-FLAT-4) | 4 | 8 |
| Men's Standard Townhome 4bd/4ba (M-STH-4) | 4 | 8 |
| Men's Oversized Townhome 4bd/4ba (M-OTH-4) | 4 | 8 |

**Private rooms.** Some students pay extra for a bedroom and bathroom to themselves (Samuel/manager input), so a
2-bedroom with one private tenant holds 3. PROPOSED TECHNICAL DEFAULT: count **bedroom slots**: shared tenant = 1,
private tenant = 2, apartment = 2 x bedrooms. Check: `2 x private + shared <= 2 x bedrooms`, and headcount at most
`capacity_override` when set. This only checks that a valid bedroom split *exists*; tenants still choose bedrooms
themselves (AC3). QUESTION FOR LODGE CONFIRMATION: is the private option on the contract; may a shared-rate tenant
end up alone in a bedroom; should the override be in people or bedrooms (E16 says "one sleeping area unavailable")?

### 4.2 Where the inputs come from (the Lodge's two forms)

**Form 1** is the website application (before the deposit). **Form 2** is the roommate-matching form (after the
security deposit, before the semester). Field-by-field privacy handling is in [data-privacy.md](data-privacy.md).

| Engine input | Comes from | Used for |
|---|---|---|
| Contracted floor plan, term | The signed contract (Form 1 shows the requested plan and semester) | H1 |
| Self-reported allergy concern | Allergy question on **both** forms (Yes/No): Form 1 about living with an assistance animal, Form 2 about a roommate with an ESA/service animal | Tier 0b, review item (4.3) |
| Staff-confirmed allergy | Staff, after the Lodge manager receives documentation by email | H5 |
| Has an approved animal | **Not on either form**: staff enter it from the Lodge's Reasonable Accommodation Process | H5, tier 0b |
| "If yes, please explain your condition" | Form 2, optional free text | **Never imported or stored** (health information) |
| Events & Amenities page, last page | Form 2 | **Not stored**; doesn't affect matching |
| Second semester signup ($50 off both leases) | Form 1 | S1 continuity |
| Roommate request (free text) | Form 1 and Form 2 ("people to live with") | S2, S5 |
| Current apartment number (or N/A) | Form 2, checked against Lodge records | S1, S3 |
| Other requests (e.g. "124th ward") | Form 2 free text, reviewed by staff | S9 |
| Cleanliness 1-5 | Form 2 | S8 |
| Typical bedtime: 8-9 pm, 10-11 pm, 12-1 am, later than 1 am | Form 2 | S4 |
| Shy/Outgoing, school year, age, major | Form 2 (age also on Form 1) | S6 extras |
| Gender | Form 1 | **Not stored** (AP5); a plan/gender mismatch is only flagged at import |
| Waitlist checkbox, parking pass | Form 1 | **Out of scope** (AP18) |

PROPOSED TECHNICAL DEFAULT: if Form 1 and Form 2 name different roommates, Form 2 (the later one) counts unless staff
say otherwise. Note for the Lodge: the bedtime buckets skip 9-10 pm.

### 4.3 Animals and allergies

What the Lodge's Form 2 says (paraphrased by Samuel): the Lodge allows assistance animals/ESAs with proper
paperwork through its **Reasonable Accommodation Process**; it states it will not move the tenant with the ESA out of
the apartment and cannot move a tenant out without proper documentation of a known allergy or extenuating
circumstances; students with allergies or medical conditions that prevent living with an ESA roommate must email
documentation to the Lodge manager. **We do not interpret this text legally or turn it into engine policy on our own.**

| Situation | PROPOSED TECHNICAL DEFAULT | QUESTION FOR LODGE CONFIRMATION |
|---|---|---|
| Tenant answered **Yes** on either form | Self-reported concern: avoided strongly (tier 0b, 5.4) and shown as a "needs staff review" item when it conflicts with an animal holder. Not a hard rule. | Is a Yes without documentation treated as anything today? |
| Staff confirm the documentation the manager received | Hard rule H5. Only who/when is recorded, no details. | Who may confirm, in which screen? What if it arrives after a draft or after assignments are final? |
| Tenant has an approved animal | `has_animal` entered by staff from the Reasonable Accommodation Process, never from a form. | Can staff provide a yes/no list each term? Are other animals allowed? |
| Approved animal holder renews or stays | **No automatic lock.** The case is flagged; staff may lock it manually. | Is the animal holder always kept in place (including across terms)? May they choose to move? |
| Exception to H5 for a specific pair | Recorded per-pair override (who/when). | Who is authorized to approve an override? |

The documentation and the condition explanation **never enter Roomatch** ([data-privacy.md](data-privacy.md)).

## 5. The points score

### 5.1 Weights: Samuel's original and the recommended revision

All recommended values are **PROPOSED TECHNICAL DEFAULT (AP7)**: not approved. Samuel's originals stay visible.
Points = weight x 100 per tenant at full satisfaction. "Closeness" (AP11): same answer = 1, one step apart = 0.5,
two or more steps = 0, averaged over the tenant's roommates; missing answers are handled as in 5.5.

| Code | Preference | Samuel's original | Recommended | Tier | How a tenant earns the points | Reason |
|---|---|---|---|---|---|---|
| (H5) | Avoid animal/allergy conflicts | .98 | hard after confirmation; tier 0b before | 0b | Never traded for preferences (5.3). | A weight can be outbid; the Lodge's process relies on documentation. |
| S1 | **Continuity:** stays in the same apartment, because (a) a renewing tenant asked to stay, or (b) they signed for two semesters | .95 (renewal stay) | .95, **at most once per tenant** | 1 | All or nothing; only if the apartment's plan matches the new contract (not E11). | Merges the old S10 so the same outcome is not rewarded twice (AP8, below). |
| S10 | Two-semester continuity | none (new) | **not scored separately** | - | Counts as source (b) of S1. | Kept for traceability. |
| S2 | **Mutual** roommate requests | .90 | .90 | 1 | 90 x (mutual partners placed with them / mutual partners requested). | Unchanged. |
| S3 | Renewal moves stay **nearby** (same floor AND same ward) | .85 | .85 | 1 | 85 if same floor and same ward; while ward data is missing, same floor counts. | Unchanged. Adjacent-floor credit is a Lodge question. |
| S8 | **Cleanliness** (1-5) | none (nearest: extras .30) | .75 | 2 | 75 x closeness. | A specific form field and a common cause of conflict. |
| S4 | **Bedtime** (4 ordered buckets) | .70 ("sleep schedules") | .70 | 2 | 70 x closeness. | Weight unchanged; uses the form's buckets. |
| S7 | Non-needing tenants avoid accessible apartments | none | -.60 | 2 | -60 per non-needing tenant in an `is_accessible` apartment. | Only if the Lodge does not require strict reservation (H6 question). |
| S5 | **One-way** roommate requests | .55 | .55 | 2 | 55 x (requested people placed with them / requested). Requester only. | Unchanged. |
| S9 | **Ward request** (e.g. "124th ward") | none (new) | .50 | 2 | 50 if placed in the staff-confirmed requested ward. | A place preference, weaker than a people preference. Needs the ward map. |
| S6 | **Extras**, total | .30 | .30 total | 3 | Sum of S6a-S6d. | Samuel's total kept. |
| S6a | Shy/Outgoing (binary) | (in extras) | .10 | 3 | 10 if same answer, else 0. | Whether "same" is better is unvalidated. |
| S6b | School year (Freshman to Senior) | (in extras) | .08 | 3 | 8 x closeness. | Low signal. |
| S6c | Age | (in extras) | .07 | 3 | 7 if within 2 years, 3.5 if within 4, else 0. | Low signal. |
| S6d | Major | (in extras) | .05 | 3 | 5 if the same (case-insensitive, trimmed). | Free text; lowest. |

**AP8, no double counting (PROPOSED TECHNICAL DEFAULT, for approval).** A renewing tenant who also signed for two
semesters would otherwise collect 95 + 95 = 190 for one outcome (staying put), which would make them twice as hard to
move as any other renewal. Proposed: one continuity line per tenant, 95 at most, whatever the number of reasons; the
explanation lists the reasons. Alternative for the team: make two-semester continuity a hard rule or a manager lock,
if the Lodge says it is a promise (QUESTION FOR LODGE CONFIRMATION).

Per tenant rather than per pair (AP6), so big groups don't collect more points just by having more pairs: a full mutual
group of 4 earns 4 x 90 = 360, not 6 pairs x 90 = 540.

### 5.2 The danger: many small points beating one big one

In a plain sum, many low-weight points outvote one high-weight point. A renewal stay earns 95; if moving her lets
five roommates improve bedtime closeness from 0.5 to 1.0, they gain 5 x 35 = 175, and a plain sum moves her.

### 5.3 Hard rule vs a .98 weight

A weight can be outbid: at 98 points, an allergy conflict would be "worth it" if it unlocked two mutual pairs
(4 x 90 = 360). That is why confirmed allergies are a hard rule with a recorded override, and unconfirmed ones sit in
their own tier (0b) above all preferences.

### 5.4 Tiers, with points inside each tier (PROPOSED TECHNICAL DEFAULT, AP9)

| Tier | Contents | Compared how |
|---|---|---|
| 0 | Number of contracted tenants placed | First. |
| 0b | Number of self-reported (unconfirmed) allergy concerns sharing with an approved animal holder | Minimized right after tier 0; each remaining one is a review item. |
| 1 | S1 continuity (95), S2 mutual requests (90), S3 nearby renewal moves (85) | Points added up inside the tier. |
| 2 | S8 cleanliness (75), S4 bedtime (70), S7 accessible-apartment penalty (-60), S5 one-way (55), S9 ward request (50) | Only to choose between plans tied on tiers 0 to 1. |
| 3 | S6 extras (30 total) | Only to break remaining ties. |

Plans are compared tier by tier: more tier-1 points always wins, however many tier-2 or tier-3 points the other has.

**Tiers do not protect renewal stays inside tier 1.** A renewal stay (95) still loses to combined points in the same
tier. Examples: moving one staying renewal out can let two other tenants each complete a mutual request
(2 x 90 = 180 > 95), or complete one mutual request and one nearby move (90 + 85 = 175 > 95). The engine would then
move the renewal. QUESTION FOR LODGE CONFIRMATION: **which stays are guaranteed and which are only preferred?**
Guaranteed stays should become manager locks (H7) or a hard rule; preferred stays keep 95 points and can lose as
above.

| Option | Pros | Cons |
|---|---|---|
| **Tiers (proposed)** | Cross-tier problem (5.2) can't happen; easy to explain. | Rigid across tiers; still trades inside tier 1 (above); tier boundaries are a decision. |
| Single sum with per-tenant caps | One number; smooth. | The 5.2 example still flips. |
| Single sum with steep multipliers | Like tiers, one number; easy for an exact solver. | Arbitrary multipliers; hard to explain. |

All weights, tiers and defaults are stored with each draft (section 10).

### 5.5 Missing answers are not automatically neutral (PROPOSED TECHNICAL DEFAULT, AP10)

The earlier draft said "missing data never earns or loses points, so it is neutral". That is **not true** for
pairwise lines (cleanliness, bedtime, extras):

- **Free filler effect.** If missing answers are simply left out of the average, a tenant with no cleanliness answer
  placed with two "5"s leaves their averages unchanged, so the engine sees them as just as good as another "5".
  Non-answerers become free filler for any apartment, and answerers who differ look *worse* than people who said
  nothing.
- **Incentive.** If students learned this, skipping a question would be "safer" than answering honestly.
- **Unequal maximums.** A tenant who answered everything can earn up to 75 + 70 + 30 extra points; a non-answerer earns
  0 there. Totals and averages are not comparable across drafts or groups with different coverage.
- Requests (S2, S5, S9) are different: no request is not missing data. Tenants who make requests can gain points
  others can't; that is intended, but it means requesters are favored and should be stated openly.

**Proposed default:** when either side of a pairwise comparison is missing, that pair gets a fixed **0.5** (the
same as "one step apart"). A non-answerer is then neither a magnet nor a repellent. A required field that is missing
(for example the allergy Yes/No) is **never** treated as "No": it becomes a `missing_data` review item.

**Report data coverage separately** from scores: the share of tenants who answered each field, overall and per plan,
shown next to every draft summary.

Fictional test cases for this are in section 17 (documented only; not implemented).

### 5.6 Apartment-level compatibility (CONFIRMED PRODUCT DECISION, AC12)

Because tenants choose bedrooms themselves (AC3), compatibility is scored across **all roommates in the apartment**.
Scoring who would share a bedroom ("bedroom pairs") would require predicting choices the Lodge leaves to tenants. It
is an unvalidated alternative, not an approved improvement.

## 6. Roommate request resolution (before any placement)

Requests are stored with their **original wording** (`raw_text`, never overwritten). Generated tenant codes don't
resolve duplicate names: two different people can have the same name.

**Identity rule (PROPOSED TECHNICAL DEFAULT, AP12).** A request is resolved automatically only if the normalized name
(case-insensitive; "First Last" or "Last, First") matches **exactly one eligible tenant in the target term** (someone
with a contract in that term). Zero matches, several matches, or only near matches all go to staff review. Where the
Lodge's own resident ID is available, use it for identity across forms and terms (privacy doc D4).

| Situation | Example | What happens | What staff see |
|---|---|---|---|
| Exact, unique, eligible target-term match | | `resolved` | Nothing unless other checks fail. |
| Exact match, but two or more people in the term have that name | | `ambiguous` | Review item listing every candidate. |
| Likely misspelling (one close match) | E03 | Suggested only; not used until staff confirm. | Review item with wording and suggestion. |
| Ambiguous (e.g. first name only) | E06 | `ambiguous`, not used. | Review item listing candidates. |
| Person does not exist in the term | E04 | `unresolved`, not used. | Review item with the original wording. |
| Person is in the other building | E07 | `excluded` (H1). | Review item with the reason. |
| Person contracted a different plan | E08 | `excluded`; would need a contract change. | "Contract change needed?" |
| Blocked by a hard rule or a self-reported allergy concern | E14, E02 | `excluded` with the rule or concern. | Review item; confidential reasons not shown. |

Unresolved and excluded requests are **kept and shown with their reason** (AC6); they are never silently dropped.

## 7. Group requests bigger than the apartment

A mutual group can be larger than the apartment (E01: 5 people, W-STD-2 holds 4). The engine proposes the best-scoring
split, keeps every request, and puts the group in the review queue. Under per-tenant scoring 4 + 1 (270) beats 3 + 2
(180). QUESTION FOR LODGE CONFIRMATION: is 4 + 1 acceptable, and should the separated person stay on the same floor?

## 8. Wards

- Samuel believes each apartment's ward is **fixed across semesters** (Samuel/manager input; QUESTION FOR LODGE
  CONFIRMATION). A tenant's ward then follows from placement.
- A floor can hold parts of several wards; a ward spans both buildings (Samuel's ward: part of men's floor 4 and part
  of women's floor 1; one example, not a rule).
- PROPOSED TECHNICAL DEFAULT: `apartments.ward_id`, nullable until staff provide the mapping; ward requests from
  Form 2 free text are resolved by staff like names, and only confirmed ones score (S9).
- QUESTION FOR LODGE CONFIRMATION: may tenants request a ward; do ward sizes need balancing?

## 9. The search, and what it can't prove (PROPOSED TECHNICAL DEFAULT, AP14)

1. **Validate inputs**: locks obey the hard rules, requests resolved, enough slots per plan.
2. **Keep locks.**
3. **Seed continuity** (S1) where the plan still matches.
4. **Seed mutual groups**, largest first; split oversized groups (section 7).
5. **Fill the rest** greedily by points (renewal movers first).
6. **Improve** with single moves and pair swaps within a plan while the tiered score improves, up to a time limit.

**Limitations.** Greedy placement plus single moves and pair swaps can miss valid solutions: some improvements, or
even some feasible placements, need three or more tenants to move at once. So the engine must report two different
things:

| Status | Meaning | When it may be shown |
|---|---|---|
| **Proven impossible** | No valid placement exists under the hard rules. | Only with a proof the engine can state: e.g. a plan has more contracts than slots ("at least 2 of these 10 cannot be placed"), or a tenant's restrictions exclude every apartment of their plan. |
| **Search did not find a placement** | The search stopped without placing this tenant. A valid placement might still exist. | All other cases. Staff try manually; a later exact solver can settle it. |

Likewise a draft's score is "best found", never "best possible", unless an exact solver proves it.

**Exact solver (later):** Google OR-Tools CP-SAT runs locally and can prove optimality or infeasibility; tiers become
sequential solves. Tradeoffs: one large native dependency, harder explanations, speed not yet measured. No cloud
services or external APIs either way (AC5).

## 10. Tie-breaking and reproducibility (PROPOSED TECHNICAL DEFAULT, AP15)

- Fixed processing orders (tenant code, apartment number, move order); exact ties keep the first plan found.
- Every generated draft records: the **algorithm version** (release number and git commit), the **configuration
  version** (weights, tiers, missing-answer rule, time limit; stored in `priority_config` with a version and hash),
  the **input version** (a hash or snapshot ID of the contracts, preferences, resolved requests, restrictions, locks
  and apartments used), any **random seed**, and the generation start and end time.
- Same input version + configuration version + algorithm version must give the same draft. A seed alone is not
  enough.
- Who stays unplaced in an oversubscribed plan (E15) is policy, not a tie-break: QUESTION FOR LODGE CONFIRMATION
  (contract date, renewals first, staff choice). Until then the engine shows the shortfall and staff choose.

## 11. Explanations (PROPOSED TECHNICAL DEFAULT, AP16)

- Points breakdown by line, including points the tenant could have earned and didn't, and the data coverage used.
- "Why not better": the best alternative apartment and the hard rule, lock or tradeoff that blocked it.
- Confidential reasons are never shown, only that a staff rule applied.

Example (fictional): *"Placed in 3616 (W-STD-3, floor 1). Continuity kept: renewal stay (+95). Mutual request with
Parker Tumblewhump kept (+90). Bedtime: both roommates in your bucket (+70)."*

## 12. Later workflows (after automatic drafts)

**Improve existing draft (PROPOSED TECHNICAL DEFAULT, AP17).** Staff often start from a draft they already edited.
This workflow takes that draft as the starting point, keeps locks and (by default) every manually placed tenant, and
looks for improvements while **minimizing the number of tenants moved**. It does not apply changes by itself: it
presents a short list of tradeoffs ("moving 2 tenants gains 2 mutual requests; moving 5 also fixes 3 bedtime
mismatches"), and staff accept or reject each one. Each accepted change is recorded like any manual edit (AC7).

**What-if alternatives and manager-approved preference adjustments** come later (AC4). Weight changes are made only by
a manager, never learned automatically (AC5). Undone edits and edits without a reason are not used as evidence (AC10).

## 13. Worked examples from the test data

Small seed (`db/seed_small.sql`, seed 2027); answer key `db/seed_small_answer_key.json`. All fictional.

| Case | Situation | How the design handles it |
|---|---|---|
| **E01** group too big | Five W-STD-2 tenants (FAKE-00001 to 00005) request each other; W-STD-2 holds 4 (apartments 5555, 5842, 8314). | Not all five fit (H2). 4 + 1 scores 270, 3 + 2 scores 180, so 4 + 1 is proposed; who is left out is only a tie-break, so it goes to review. |
| **E02** animal vs allergy | Cyrus Puddlebeam (FAKE-00006, has an animal) and Vale Wafflebean (FAKE-00007, allergy flag) request each other, M-STD-3. | The seed has no confirmation fields, so Vale's flag counts as self-reported: tier 0b keeps them apart, the request is not honored, and a review item explains why. Confirmed documentation would make it hard (H5). Nothing locks Cyrus automatically. |
| **E03** misspelling | Harper Pebblebottom (FAKE-00008) wrote "Quinn Jelyberry"; Quinn Jellyberry (FAKE-00009) wrote Harper correctly. | Suggest FAKE-00009 for staff confirmation; until then only Quinn's request counts (one-way). |
| **E06** ambiguous | Piper Kazoonugget (FAKE-00013) wrote "Jordan"; two Jordans in M-FLAT-4. | `ambiguous`; review lists both. |
| **E07** cross-building | Indigo Crumpetbeam (FAKE-00016, W-STD-3) requests Lark Jellynugget (FAKE-00017, M-STD-3). | Excluded (H1) with the reason. |
| **E08** different plan | FAKE-00018 (M-STD-3) and FAKE-00019 (M-OTH-4) request each other. | Flagged "contract change needed?"; contracts are never edited. |
| **E09** renewal pair stays | Wren Moontooth (FAKE-00020) and Parker Tumblewhump (FAKE-00021) live in 3616 and want to stay together. | Seeded first; each earns 95 + 90. Still only "preferred" unless the Lodge says stays are guaranteed (5.4). |
| **E10** renewal move | Kestrel Moonwhump (FAKE-00022) in 5793 (M-STD-3, floor 1) wants to move. | The only other M-STD-3 apartment, 7679, is on floor 2: S3 earns 0, and the explanation says so. |
| **E11** plan changed | Kestrel Kazootoes (FAKE-00023) lives in 7679 (M-STD-3) but contracted M-FLAT-4. | Staying breaks H1; flagged; treated as a move; 2620 (M-FLAT-4, floor 2) earns S3. |
| **E14** restriction vs request | FAKE-00027 requests FAKE-00026; staff confirmed they must not share. M-OTH-4. | H4 wins; request excluded. M-OTH-4 has one apartment, so at most one of them can be placed. |
| **E15** oversubscribed | M-OTH-4: 10 contracts, 8 places (7215). | "Proven impossible" for 2 of the 10 (count proof); because of E14 one of them must be FAKE-00026 or FAKE-00027. Which ones is a Lodge question. |

## 14. Fairness

- Per-tenant points, so large groups don't dominate.
- Missing answers handled explicitly (5.5), with coverage reported; they are not assumed neutral.
- Extras are capped low; "similar" is not assumed to mean "better roommates".
- Renewals get priority (S1, S3); how much is a QUESTION FOR LODGE CONFIRMATION.
- No hidden rules; confidential reasons are shown only as "staff restriction".
- Draft summaries show the distribution (how many got none of their requests), not only totals.
- Private rooms are paid and reduce capacity by design.

## 15. Success measures (CONFIRMED PRODUCT DECISION, AC11)

| Measure | Definition |
|---|---|
| **Placement acceptance** | Of the tenants **placed** in the original generated draft (the baseline, AC8), the percentage still in their proposed apartment in the final assignment after review. |
| **Unplaced tenants** | Reported separately: count unplaced in the baseline and in the final assignment, with "proven impossible" vs "not found" (section 9). Acceptance never includes them, so it can't hide them. |
| **Preventable corrections** | Changes that could have been avoided using information available when the draft was generated. PROPOSED TECHNICAL DEFAULT: counted from staff-chosen reasons ("Better tradeoff using existing information"); the system does not guess. |
| **New-information changes** | Tracked separately (reason "New information"). |
| **One-time exceptions** | Tracked separately. |
| **Unexplained changes** | "No reason supplied" stays **unclassified**; never counted as preventable or as evidence. |
| **Time** | Generation time (machine) and review time (staff, from opening the draft to finalizing it) measured separately and together. PROPOSED TECHNICAL DEFAULT: review time is wall-clock; active editing time is also logged if available. |

Undone edits don't count as changes. **Aspiration:** about 90% placement acceptance and under 60 minutes total
generation plus review. These are **hypotheses to validate** on fictional data first, not promised results.

## 16. Proposed schema changes (PROPOSED TECHNICAL DEFAULT; NOT applied)

None of these are in `db/schema.sql`. Workspace and history tables are listed in
[manual-workspace.md](manual-workspace.md#5-schema-mapping-and-gaps).

| Change | Purpose | Notes |
|---|---|---|
| `contracts.room_type ENUM('shared','private')` | Slot capacity (4.1). | Lodge question: is it on the contract? |
| `contracts.linked_contract_id` (or a `lease_group_id`) | S1 continuity source (b). | |
| `preferences.cleanliness TINYINT NULL` (1-5) | S8. | |
| `preferences.bedtime ENUM('8-9pm','10-11pm','12-1am','after_1am') NULL` | S4. | Replaces `sleep_schedule`. |
| `preferences.social_preference ENUM('shy','outgoing') NULL` | S6a. | Replaces three values. |
| `preferences.academic_stage ENUM('freshman','sophomore','junior','senior') NULL` | S6b. | Replaces five values. |
| `ward_requests` (contract, `raw_text`, `resolved_ward_id`, status, reason) | S9. | Mirrors `roommate_requests`. |
| `wards` + `apartments.ward_id` (nullable) | S3, S9. | Null until staff give the mapping. |
| `apartments.is_accessible BOOLEAN DEFAULT FALSE` | H6, S7. | |
| `placement_restrictions` types `requires_accessible`, `allergy_override` | H6, H5 override. | Reuse confirmed_by / confirmed_at. |
| `allergy_self_reported` + `allergy_confirmed_by/_at` (replacing `animal_allergy_restriction`) | 4.3. | No details stored. |
| `has_animal` + `animal_approved_by/_at` | 4.3. | Staff-entered. |
| `capacity_override` in slots (or an extra slot override) | E16. | Lodge question. |
| `tenants.lodge_resident_id` (nullable, unique) | Identity (AP12). | Only if the Lodge provides it. |
| `assignment_drafts.algorithm_version`, `config_version`, `input_hash` | Reproducibility (AP15). | `priority_config` already exists. |
| **Not added:** gender, birthday, emails, address, phone, emergency contact, parking, comments, marketing source, waitlist | | See [data-privacy.md](data-privacy.md). |

## 17. Fictional test cases to implement later (documented only)

These are **specifications** for later automated tests. **No test code is added in this change.** All inputs are
fictional. Expected results assume the proposed defaults (AP8, AP10, AP12, AP14); if the team changes a default, the
expected result changes with it.

| ID | Fictional input | Expected behaviour |
|---|---|---|
| TM1 | Apartment with A (cleanliness 5) and B (5). Candidates: C (cleanliness missing), D (1), E (5). | E is preferred over C, and C over D. C is **not** scored as equal to E (no free filler). |
| TM2 | Same as TM1 but with the "omit missing" rule switched on in the config. | Documents the bias: C and E tie. Used to show why AP10 was proposed. |
| TM3 | Tenant F with every optional answer missing; two otherwise identical apartments. | Same score for both; the fixed tie-break decides; the explanation says "no answers to compare". |
| TM4 | 10 tenants: 6 in W-STD-2 (5 answered cleanliness) and 4 in W-STD-3 (2 answered). | Coverage report: cleanliness 70% overall, 83% W-STD-2, 50% W-STD-3, shown separately from scores. |
| TM5 | Tenant G whose allergy Yes/No is missing in the import. | Not treated as "No"; a `missing_data` review item is created. |
| TM6 | Renewing tenant H with no current apartment in the import. | No continuity points; `missing_data` review item. |
| TM7 | Two drafts of the same term with different coverage. | Each draft summary shows coverage next to its score. |
| TI1 | Two tenants named "Sage Copperbean" in the target term; a request for "Sage Copperbean". | `ambiguous`, both listed; not auto-resolved. |
| TI2 | One "Sage Copperbean" in the target term, another only in a past term. | Resolved to the target-term tenant. |
| TC1 | Renewing tenant J who also signed for two semesters stays in place. | Continuity counts 95 once, listing both reasons. |
| TS1 | A plan with 10 contracts and 8 slots. | Exactly 2 unplaced, shown as "proven impossible (count)". |
| TS2 | A case where the only valid plan needs a three-way rotation and the search doesn't find it. | Unplaced tenant shown as "search did not find a placement", never as "impossible". |
| TR1 | The same inputs, config and algorithm version run twice. | Identical drafts; recorded versions identical. |

## 18. Questions for Lodge confirmation

1. **Guaranteed vs preferred stays:** which renewal stays are guaranteed (to become locks) and which only preferred?
2. **Two-semester signups:** is the same apartment promised for both semesters, or only the discount?
3. **Private rooms:** is the option on the contract? May a shared-rate tenant end up alone in a bedroom?
4. **Capacity override:** in people or in bedrooms?
5. **Apartment counts** per plan and floor (the test data uses placeholders).
6. **Wards:** fixed across semesters? The apartment-to-ward list? May tenants request a ward? Balance sizes?
7. **Nearby:** does an adjacent floor, or the same ward on another floor, count for anything?
8. **Renewal "move":** is putting them back in the same apartment forbidden or just unrewarded?
9. **Allergies and animals:** the documentation rule; who confirms and who may override; timing when documentation
   arrives late; whether neighbouring apartments matter; whether a Yes without documentation counts for anything.
10. **Animal holders:** are they always kept in place when they renew (including across terms)? May they choose to move?
    Can staff provide a yes/no list each term? Are other animals allowed?
11. **Accessible apartments:** must they stay reserved even when nobody needs them? Who confirms a need?
12. **Oversubscription:** who is left unplaced, and what happens to them?
13. **Groups too big:** 4 + 1 or a more even split? Keep the separated person on the same floor?
14. **Extras:** does "match" mean similar (age, shy/outgoing, major, school year)?
15. **Terms:** is a tenant returning in Fall after Spring a renewal? Are Spring contracts different?
16. **Ward requests:** how often are they honored today? Any wards students may not request?
17. **Both roommate fields:** if Form 1 and Form 2 differ, which counts?
18. **Cleanliness and bedtime:** do staff use these answers today when matching by hand?
19. **Resident ID:** can exports include the Lodge's own resident ID?

## 19. Review questions for other AI tools

1. Is per-tenant scoring the right unit? What does it get wrong for groups and chains of requests?
2. Do the tiers distort Samuel's weights? Give a scenario the manager would clearly dislike.
3. Is the tier-1 tradeoff (5.4) acceptable if stays are only "preferred"?
4. Does the 0.5 missing-answer credit (5.5) remove the free-filler effect without creating a new bias?
5. Is merging renewal stay and two-semester continuity (AP8) the right fix for double counting?
6. Is the bedroom-slot capacity check correct and complete?
7. What cheap search improvements (e.g. three-way rotations) reduce "search did not find a placement"?
8. What must the reproducibility record contain to replay a draft exactly?
9. How should "improve existing draft" weigh moves against gains?
10. Are the success measures gameable? How could acceptance look good while outcomes are bad?
11. Which PROPOSED TECHNICAL DEFAULTS should really be Lodge questions, or vice versa?
