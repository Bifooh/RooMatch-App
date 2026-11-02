# Roomatch: how tenants get assigned to apartments (DRAFT design)

**Status:** draft for team review, 2026-10-31. Nothing in this document is implemented yet. It describes *what*
the assignment engine should decide and *why*, in plain words, so it can be critiqued (including by other AI tools)
before anyone writes solver code. Revised the same day after Samuel shared the Lodge's two real student forms
(field lists only; see [data-privacy.md](data-privacy.md)); Samuel authorized adjusting his weights.

**How to read the labels**

| Label | Meaning |
|---|---|
| **(Samuel/manager input)** | A fact or priority given by Samuel, sometimes relaying the Lodge manager. |
| **(proposal)** | Our suggested design. Open to change. |
| **(open question)** | Unknown. Must be answered by Lodge staff before it is relied on. Collected in section 15. |

| **Recommended default (likely uncontroversial)** | A decision we expect reviewers to agree with. Adopt unless someone objects. |
| **Team decision needed** | A real tradeoff the team must debate. |

All names and tenant codes in the examples are **fictional** (they come from the repo's generated test data).

## 0. Decision summary

| ID | Decision | Label | Section |
|---|---|---|---|
| A1 | The contracted floor plan (and so the building) is a hard rule; the engine never changes contracts. | Recommended default | 4 |
| A2 | Capacity is counted in bedroom slots (shared = 1, private = 2, apartment = 2 x bedrooms), plus any staff override. | Recommended default | 4.1 |
| A3 | Staff restrictions, must-not-share, manager locks and staff-confirmed accessibility needs are hard rules. | Recommended default | 4 |
| A4 | A **staff-confirmed** allergy (documentation received by the Lodge manager) is a hard rule against sharing with an approved animal holder, with a recorded per-pair override. It is not a .98 weight. | Recommended default | 4.3, 5.3 |
| A15 | A tenant with an approved ESA/service animal who stays is pinned in their apartment like a lock; conflicts are solved by placing the allergic tenant elsewhere. | Recommended default | 4.3 |
| A16 | `has_animal` is a staff-entered flag for an animal approved through the Lodge's Reasonable Accommodation Process, never taken from a form. | Recommended default | 4.3 |
| A5 | Gender is not stored; the contracted plan decides the building. | Recommended default | 4.2 |
| A6 | Points are earned per tenant (not per pair), proportional to how much is satisfied; missing data is neutral. | Recommended default | 5.1 |
| A7 | Tiered (lexicographic) scoring: placed count, then tier 1, then tier 2, then tier 3. | Recommended default | 5.4 |
| A8 | Closeness scoring for ordered answers: same = full, one step apart = half, further = none (cleanliness, bedtime, school year). | Recommended default | 5.1 |
| A9 | Roommate names and ward requests: suggest matches, never auto-resolve fuzzy or ambiguous text; staff confirm. | Recommended default | 6 |
| A10 | Cross-building and different-plan requests are excluded and flagged, never forced. | Recommended default | 6 |
| A11 | Deterministic tie-breaking (fixed orders, seed stored with the draft). | Recommended default | 10 |
| A12 | Build "seed then improve" first; use an exact solver (CP-SAT) later as a quality check. Local only. | Recommended default | 9 |
| A13 | Explanations show points and the blocking rule, never a confidential reason. | Recommended default | 11 |
| A14 | The waitlist and parking are out of scope. | Recommended default | 4.2 |
| T1 | Who is left unplaced when a plan is oversubscribed (E15). | **Team decision needed** | 10 |
| T2 | The recommended weights, especially cleanliness (.75) and where it sits in the tiers. | **Team decision needed** | 5.1 |
| T3 | Multi-semester signups: is keeping the same apartment a hard promise or a .95 preference? | **Team decision needed** | 5.1 |
| T4 | Groups bigger than the apartment: 4 + 1 or a more even split? | **Team decision needed** | 7 |
| T5 | Ward requests: weight (.50 proposed) and whether ward sizes need balancing. | **Team decision needed** | 8 |
| T6 | Partial credit for an adjacent floor in renewal moves. | **Team decision needed** | 5.1 |
| T7 | Whether accessible apartments must stay reserved even when nobody needs them. | **Team decision needed** | 4 |
| T8 | Shy/Outgoing: does "match" mean the same answer? | **Team decision needed** | 5.1 |
| T9 | How much priority renewals get over new tenants (fairness). | **Team decision needed** | 13 |
| T10 | A **self-reported** allergy Yes without confirmed documentation. Recommended default: warn and avoid (tier 0b in 5.4, plus a review item); the hard rule applies only after confirmation. Alternative: hard from the start. | **Team decision needed** (with a recommended default) | 4.3 |

---

## 1. Purpose and context

The Lodge is student housing with **two buildings, Women's and Men's, each with 4 floors** (Samuel/manager input).
Each term, every tenant has a signed contract, and staff must give each contracted tenant an **apartment number**.
Today this is done by hand. Roomatch should produce a *draft* assignment that staff can review, edit, lock and
explain, then finalize.

Key facts that shape the design:

- **The contract comes first.** Tenants sign before roommates are known. The contract fixes the **floor plan** and
  the **price**, and the floor plan fixes the building (Women's or Men's) (Samuel/manager input).
- **Assignments are to apartment numbers only.** The app never picks a bedroom or a bed (Samuel/manager input).
- **Everyone is LDS.** The Lodge alone is one stake of about **6 wards, each with 150+ people**. A ward is tied to a
  set of apartments, so a tenant's ward is a *result* of where they are placed (Samuel/manager input; see section 8).
- The goal is a **good, explainable, repeatable** draft, not a black box. Staff stay in charge.

## 2. Terms

The Lodge runs **three terms: Fall, Winter and Spring. There is no Summer term** (Samuel/manager input).
Each term is assigned separately. A **renewing** tenant is someone who already lived at the Lodge in the previous
term and has a contract for the next one; their "current apartment" is where they lived in that previous term
(proposal; the Spring-to-Fall gap over the summer is an open question: is someone returning in Fall after Spring
still a "renewal" for staying purposes?).

## 3. The two-layer model

The engine works in two layers.

1. **Hard rules** (section 4). Yes/no checks that can never be broken by the solver. A plan that breaks any hard
   rule is invalid, full stop. If the rules make it impossible to place someone, the engine leaves that person
   **unplaced and says why**; it never bends a rule quietly.
2. **A points score** (section 5). Among all plans that pass every hard rule, the engine prefers the one with the
   most points. Points come from weighted preferences (renewals, roommate requests, sleep schedules, and so on).
   Every point a tenant earns or misses is listed, so each placement can be explained.

Why not only if-statements? Because preferences conflict all the time (two friends want the same last spot, a
renewal blocks a group). Points let the engine *weigh* those conflicts consistently instead of following whichever
if-statement happens to run first. Why not only points? Because some things (wrong building, over capacity, an
animal next to a severe allergy) must never be "worth it", no matter how many points are on the other side.

## 4. Hard rules

| # | Rule | Source | Notes |
|---|---|---|---|
| H1 | **Contracted floor plan.** A tenant may only be placed in an apartment of the floor plan on their contract. This also fixes the building (Women's/Men's). | Samuel/manager input | The engine never changes a contract. A request that needs a different plan becomes a staff flag (E08, E11). |
| H2 | **Capacity, in bedroom slots.** See 4.1. | Samuel/manager input (2 per bedroom, private rooms); slot model is a proposal | Includes the staff `capacity_override`. |
| H3 | **Staff placement restrictions**: required/excluded apartment, required/excluded floor. | Existing schema (`placement_restrictions`) | Entered only after staff confirm. The confidential reason is never stored. |
| H4 | **must_not_share_with**: the two tenants may not be in the same apartment. | Existing schema | Beats any roommate request (E14). |
| H5 | **Animal vs confirmed allergy.** A tenant whose allergy is **staff-confirmed** (documentation received by the Lodge manager) may not share an apartment with a tenant who has an **approved** animal. | Samuel/manager input: weight .98, "people are very passionate about it"; the Lodge's ESA policy on Form 2 | Hard unless staff override it for that pair (5.3). A self-reported Yes alone is not hard; see 4.3. Whether neighbouring apartments matter is an open question. |
| H9 | **Approved animal holders are not moved out.** A tenant with an approved ESA/service animal who stays in (or renews into) their apartment is pinned there like a manager lock. | The Lodge's ESA policy on Form 2 (via Samuel) | Our reading of "will not move the tenant with an ESA out" (4.3). |
| H6 | **Accessibility need.** A tenant with a **staff-confirmed** accessibility need must get a handicap-adapted apartment of their plan. | Samuel/manager input: a few first-floor apartments are adapted; rule is a proposal | If their plan has no free adapted apartment, they go to the review queue. The opposite direction (others avoiding these apartments) is a soft rule, S7. |
| H7 | **Manager locks.** A locked assignment is kept exactly as is. | Existing schema (`is_locked`, `locked_by`) | If a lock itself breaks another hard rule, the engine refuses to build the draft and shows the conflict; it does not move the lock. |
| H8 | **One apartment per contract per draft**, and nobody is moved to another plan to "make room". | Existing schema | Unplaced tenants are reported honestly (E15). |

### 4.1 Capacity in bedroom slots

Every bedroom holds **2 people by default** (Samuel/manager input). That gives these plan capacities:

| Plan | Bedrooms | Default headcount | Source |
|---|---|---|---|
| Women's Standard 2bd/2ba (W-STD-2) | 2 | 4 | Samuel/manager input |
| Women's Oversized 2bd/2ba (W-OVR-2) | 2 | 4 | Samuel/manager input |
| Women's Standard 3bd/3ba (W-STD-3) | 3 | 6 | Samuel/manager input |
| Women's Flat 4bd/4ba (W-FLAT-4) | 4 | 8 | Samuel/manager input |
| Women's Standard Townhome 4bd/4ba (W-STH-4) | 4 | 8 | Samuel/manager input |
| Women's Oversized Townhome 4bd/4ba (W-OTH-4) | 4 | 8 | Samuel/manager input |
| Men's Standard 3bd/3ba (M-STD-3) | 3 | 6 | Samuel/manager input (confirmed 2026-10-31) |
| Men's Flat 4bd/4ba (M-FLAT-4) | 4 | 8 | Samuel/manager input |
| Men's Standard Townhome 4bd/4ba (M-STH-4) | 4 | 8 | Samuel/manager input |
| Men's Oversized Townhome 4bd/4ba (M-OTH-4) | 4 | 8 | Samuel/manager input |

**Private rooms.** Some students pay extra to have a bedroom and bathroom to themselves (Samuel/manager input). So
the real headcount of an apartment varies: a 2-bedroom with one private tenant holds 3 people, not 4. We
**presume the private option is part of the contract**, like the plan and price (open question).

The proposal is to count capacity in **bedroom slots** (half-bedrooms) instead of people:

- a **shared** tenant uses **1 slot** (half a bedroom);
- a **private** tenant uses **2 slots** (a whole bedroom);
- an apartment has **2 x bedrooms** slots.

**Feasibility check:** `2 x (private tenants) + (shared tenants) <= 2 x bedrooms`, **and** the headcount is at most the
staff `capacity_override` when one is set. This is exactly the same as "private tenants each get a whole bedroom and
the shared tenants fit two per remaining bedroom". Examples for a 4-bedroom (8 slots): 8 shared; 1 private + 6 shared
(7 people); 2 private + 4 shared (6 people); 4 private (4 people).

Two consequences to confirm: (a) if the shared count is odd, one shared tenant ends up alone in a bedroom without
paying for private (is that allowed, and does it matter?); (b) the existing `capacity_override` is a **headcount**,
but E16's reason is "one sleeping area unavailable", which is really a loss of bedroom slots. We may want the
override expressed in slots instead (open question).

### 4.2 Where the inputs come from (the Lodge's two forms)

The Lodge has two student forms (Samuel/manager input; field lists only, no values kept). **Form 1** is the website
application filled before the deposit. **Form 2** is the roommate-matching form, filled after the security deposit
is paid and before the semester. The full field-by-field privacy table is in [data-privacy.md](data-privacy.md).

| Engine input | Comes from | Used for |
|---|---|---|
| Contracted floor plan, term | The signed contract (Form 1 shows the requested plan and semester) | H1 |
| Self-reported allergy concern | Allergy question on **both** forms (Yes/No): Form 1 asks about living with an assistance animal, Form 2 about a roommate with an ESA/service animal | Tier 0b, review item (4.3) |
| Staff-confirmed allergy | Staff, after the Lodge manager receives documentation by email | H5 |
| Has an approved animal | **Not on either form**: staff enter it from the Lodge's Reasonable Accommodation Process | H5, H9, tier 0b |
| "If yes, please explain your condition" | Form 2, optional free text | **Never imported or stored** (health information). |
| Events & Amenities page, last page | Form 2 | **Not stored**; doesn't affect matching. |
| Second semester signup ($50 off both leases) | Form 1 | S10 continuity |
| Roommate request (free text) | Form 1 and Form 2 ("people to live with") | S2, S5 |
| Current apartment number (or N/A) | Form 2, checked against Lodge records | S1, S3 |
| Other requests (e.g. "124th ward") | Form 2 free text, reviewed by staff | S9 ward request |
| Cleanliness 1-5 | Form 2 | S8 |
| Typical bedtime: 8-9 pm, 10-11 pm, 12-1 am, later than 1 am | Form 2 | S4 |
| Shy/Outgoing, school year (Freshman to Senior), age, major | Form 2 (age also on Form 1) | S6 extras |
| Gender | Form 1 | **Not stored.** The contracted plan already fixes the building; a mismatch is only flagged at import. |
| Waitlist checkbox, parking pass | Form 1 | **Out of scope** for assignment. |

Two things to flag: tenants who list both roommate fields (Form 1 and Form 2) may name different people, so Form 2,
the later one, should win unless staff say otherwise (proposal). And the bedtime buckets skip 9-10 pm, which could
confuse some students (a note for the Lodge, not an engine problem).

### 4.3 Animals and allergies (the Lodge's ESA policy)

Form 2 has an Emotional Support Animal information page (Samuel/manager input, paraphrased). Under the Fair Housing
Act the Lodge allows assistance animals and ESAs with proper paperwork through its **Reasonable Accommodation
Process**. It will not move the tenant with the ESA out of the apartment, and it cannot move a tenant out without
proper documentation of a known allergy or other extenuating circumstances. Students with allergies or medical
conditions that prevent living with an ESA roommate must email documentation (a doctor's note or allergy results)
to the Lodge manager.

How the engine follows this (proposal):

| Situation | Data | Engine behaviour |
|---|---|---|
| Tenant answered **Yes** to the allergy question on Form 1 (assistance-animal wording) or Form 2 (ESA/service-animal wording) | `allergy_self_reported` | **Self-reported allergy concern.** Avoided strongly (tier 0b in 5.4) and shown as a "needs staff review" item whenever it conflicts with an animal holder. Not a hard rule yet (T10). |
| The Lodge manager has the documentation and staff confirm it in the app | `allergy_confirmed_by`, `allergy_confirmed_at` (no details) | **Hard rule H5.** |
| Tenant has an animal approved through the Reasonable Accommodation Process | `has_animal`, entered by staff with who/when (no animal or condition details) | Counts for H5 and tier 0b. Never read from a form. |
| Approved animal holder stays or renews in place | as above | **H9:** pinned in their apartment. The allergic tenant is the one placed elsewhere. |
| Documentation arrives **after** a draft exists | | The draft is re-checked; a new H5 conflict becomes a review item, and the fix moves the allergic tenant, not the animal holder. Timing is an open question. |

The documentation itself, and the optional "please explain your condition" text on Form 2, **never enter Roomatch**
(see [data-privacy.md](data-privacy.md)). Only the Yes/No and the staff confirmation are used.

## 5. The points score

### 5.1 Weights: Samuel's original and the recommended revision

Samuel gave importance weights on a 0 to 1 scale and then authorized revising them using the forms (Samuel/manager
input). Each weight turns into **points = weight x 100 per tenant** at full satisfaction (proposal). "Closeness"
below means: same answer = 1, one step apart = 0.5, two or more steps apart = 0, averaged over the roommates who
answered; tenants who didn't answer are left out of the average.

| Code | Preference | Samuel's original | Recommended | Tier | How a tenant earns the points | Reason for the change |
|---|---|---|---|---|---|---|
| (H5) | Avoid animal/allergy conflicts | .98 | hard rule once documentation is confirmed; tier 0b before that | 0b | Confirmed: never traded, staff override only (5.3). Self-reported: minimized above all preferences (5.4). | A weight can be outbid; the manager's intent is "almost never". The Lodge's policy requires documentation before it acts. |
| S1 | Renewing tenant who wants to **stay** stays in their apartment | .95 | .95 | 1 | All or nothing, only if the apartment's plan matches the new contract (not E11). | Unchanged. |
| S10 | **Signed for two semesters**: same apartment in the second term | none (new) | .95 | 1 | All or nothing, scored like S1 in the second term. | Form 1 offers "$50 off both leases"; moving them mid-year is like breaking a renewal stay. **Flag (T3):** the Lodge may treat it as a promise, which would make it a hard rule or a manager lock. |
| S2 | **Mutual** roommate requests | .90 | .90 | 1 | 90 x (mutual partners placed with them / mutual partners requested). | Unchanged. |
| S3 | Renewal moves stay **nearby** (same floor AND same ward) | .85 | .85 | 1 | 85 if same floor and same ward; while ward data is missing, same floor counts. | Unchanged. Adjacent-floor partial credit is T6. |
| S8 | **Cleanliness** match (1-5) | none (it is not on Samuel's list; nearest was extras .30) | .75 | 2 | 75 x closeness on the 1-5 scale. | A real, specific form field and a common cause of roommate conflict, so it deserves more than an extra. Kept just above bedtime. **Team decision (T2).** |
| S4 | **Bedtime** match | .70 ("sleep schedules") | .70 | 2 | 70 x closeness on the 4 ordered buckets (8-9 pm, 10-11 pm, 12-1 am, later than 1 am). | Weight unchanged; scoring now uses the form's real buckets instead of early/typical/late. |
| S7 | Non-needing tenants avoid accessible apartments | none (proposal) | -.60 | 2 | -60 per tenant without a need placed in an `is_accessible` apartment. | Keeps the few adapted apartments free when avoidable. Whether they must be reserved is T7. |
| S5 | **One-way** roommate requests | .55 | .55 | 2 | 55 x (one-way requested people placed with them / requested). Requester only. | Unchanged. |
| S9 | **Ward request** honored (e.g. "124th ward") | none (new) | .50 | 2 | 50 if placed in an apartment of the staff-confirmed requested ward. | Form 2 invites it as free text. A place preference is weaker than a people preference, so it sits just below one-way requests. Needs the apartment-to-ward map. **Team decision (T5).** |
| S6 | **Extras**, total | .30 | .30 total | 3 | Sum of the four lines below. | Samuel's total kept, split by how useful each field is. |
| S6a | Shy/Outgoing | (in extras) | .10 | 3 | 10 if same answer, else 0. | Binary, so closeness doesn't apply. Whether "same" is better is T8. |
| S6b | School year (Freshman to Senior) | (in extras) | .08 | 3 | 8 x closeness. | Low signal. |
| S6c | Age | (in extras) | .07 | 3 | 7 if within 2 years, 3.5 if within 4, else 0. | Comes straight from the form; low signal. |
| S6d | Major | (in extras) | .05 | 3 | 5 if the same (case-insensitive, after trimming). | Free text, so matching is rough; lowest weight. |

Per tenant rather than per pair, so a big apartment or a big friend group does not collect more points just by having
more pairs. A full mutual group of 4 earns 4 x 90 = 360 (four happy people), not 6 pairs x 90 = 540.

Missing data never earns or loses points (it just leaves that line out), so optional fields stay optional.

### 5.2 The danger: many small points beating one big one

A plain sum lets many low-weight points outvote one high-weight point. Example: a renewing tenant who wants to stay
earns S1 = 95. Suppose moving her out would let five other people in that apartment improve their sleep match from
0.5 to 1.0: each gains 70 x 0.5 = 35, total 175. A plain sum says "move the renewal", which is surely not what the
manager meant by .95 vs .70. Extras (S6) and the other tier-2 lines are even more dangerous because nearly everyone has a few of them.

### 5.3 Hard rule vs a .98 weight

A weight, however high, can be outbid. At 98 points, an allergy conflict would be "worth it" if it unlocked two
mutual pairs (4 x 90 = 360 points). That is why H5 is a **hard rule with an explicit staff override** instead of a
98-point score: the system never trades it away; only a named staff member can, for one specific pair, and that
override is recorded (proposed schema change in section 14). The .98 tells us how strongly the manager feels; the
override is how "98% not 100%" is expressed safely. Before documentation is confirmed, a self-reported allergy sits in
its own tier (0b) instead, so it still can't be traded for roommate preferences.

### 5.4 Proposal: tiers, with points inside each tier

| Tier | Contents | Compared how |
|---|---|---|
| 0 | Number of contracted tenants placed | First. Always place as many people as the hard rules allow. |
| 0b | Number of self-reported (unconfirmed) allergy concerns sharing with an approved animal holder | Minimized right after tier 0. Each remaining one is a review item. |
| 1 | S1 renewal stay (95), S10 two-semester continuity (95), S2 mutual requests (90), S3 nearby renewal moves (85) | Points added up inside the tier. |
| 2 | S8 cleanliness (75), S4 bedtime (70), S7 accessible-apartment penalty (-60), S5 one-way requests (55), S9 ward request (50) | Only used to choose between plans that tie on tiers 0 and 1. |
| 3 | S6 extras (30 total: shy/outgoing, school year, age, major) | Only used to break ties on tiers 0 to 2. |

Plans are compared **tier by tier** (lexicographic): a plan with more tier-1 points always wins, however many tier-2
or tier-3 points the other one has. Inside a tier, Samuel's weights decide the trade-offs (one renewal stay, 95, beats
one tenant's mutual request, 90).

**Tradeoffs**

| Option | Pros | Cons |
|---|---|---|
| **Tiers (recommended)** | Guarantees the 5.2 problem cannot happen across tiers. Easy to explain ("we kept your renewal; sleep only breaks ties"). | Rigid: 1 tier-1 point beats 1,000 tier-2 points, so a tiny gain for one person can cost many people their sleep match. The tier boundaries become a new decision for the team. |
| **Single sum with per-tenant caps** | One number; trades off smoothly. Caps (each tenant can earn at most 75 from S8, 70 from S4, 55 from S5, 30 from S6) limit the damage. | Does not remove the problem: one tenant's 95 can still lose to several other tenants' capped points (the example in 5.2 still flips). |
| **Single sum with steep weights** (e.g. tier 1 x 1,000, tier 2 x 10) | Behaves like tiers but still one number; easy for an exact solver. | The multipliers are arbitrary and must be large enough for the dataset size; hard to explain. |

**Why the tiers stay after the revision:** the forms added more tier-2 lines (cleanliness, ward), which makes
the 5.2 risk *bigger* in a plain sum: four tier-2 lines across six roommates can easily total more than one renewal
stay. Nothing in the forms argues for mixing the tiers.

A middle ground worth debating: tiers 0 to 2 as above but merge tier 3 into tier 2 with a per-tenant cap, since extras
are low-stakes either way.

All weights and the tier layout live in `assignment_drafts.priority_config`, so each draft records the settings that
produced it and staff can re-run with different settings.

## 6. Roommate request resolution (before any placement)

Requests are typed by tenants as free text and stored with their **original wording** (`raw_text`, never
overwritten). Before the solver runs, each request is resolved to a tenant or set aside:

| Situation | Example | What happens | What staff see |
|---|---|---|---|
| Exact match (case-insensitive; "First Last" or "Last, First") | | `resolved` | Nothing unless other checks fail. |
| Likely misspelling (one close match, small spelling distance) | E03 | **Suggested**, not auto-resolved. Not used until staff confirm. | Review item with the wording and the suggested tenant. |
| Ambiguous (several people fit, e.g. first name only) | E06 | `ambiguous`, not used. The engine never guesses. | Review item listing every candidate. |
| Person does not exist | E04 | `unresolved`, not used. | Review item with the original wording. |
| Person is in the other building | E07 | `excluded`: cannot share (H1). | Review item with the reason. |
| Person contracted a different plan | E08 | `excluded` for matching: would need a contract change, which the engine never makes. | Review item "contract change needed?". |
| Blocked by a hard rule (must_not_share, confirmed allergy) or a self-reported allergy concern | E14, E02 | `excluded` with the rule or concern as reason. | Review item; the confidential reason is not shown. |

After resolution, a request is **mutual** if both tenants resolved to each other, otherwise **one-way**. The schema
already has the statuses (`pending`, `resolved`, `unresolved`, `ambiguous`, `excluded`) and the review queue types.

## 7. Group requests bigger than the apartment

A friend group (people linked by mutual requests) can be larger than the apartment, for example 5 people asking for
a 2-bedroom that holds 4 (E01), or a group that fits by headcount but not once someone's private room is counted.
The engine cannot place them all together. It does **not** silently split them: it chooses the split that scores
best, keeps every request on record, and puts the group in the review queue with the proposed split. Under the
per-tenant scoring in 5.1 a split of 4 + 1 scores more than 3 + 2 (see E01 below), so the solver will tend to leave one
person out. Whether that is acceptable, and whether the separated person should be kept on the same floor as the
group, are open questions.

## 8. Wards

- Each apartment belongs to one ward, and Samuel believes this is **fixed across all semesters** (Samuel/manager
  input, to confirm). A tenant's ward is therefore decided by their placement.
- A ward's apartments are physically close. A floor holds many apartments and can contain parts of several wards.
  A ward spans both buildings: Samuel's ward is part of men's floor 4 plus part of women's floor 1 (Samuel/manager
  input; one example, not a general rule).
- **Proposal:** add `apartments.ward_id`, nullable until staff provide the mapping. The engine uses it for S3
  (nearby = same floor and same ward) and shows it on every placement.
- Wards do not change roommate eligibility; building and plan already do that.
- **Ward requests** exist: Form 2's "other requests" box has the example "124th ward". The proposal treats them like
  roommate names: staff confirm which ward a text means (`resolved`, `ambiguous`, `unresolved`), and only confirmed
  requests score (S9). Free text that isn't about a ward is handled by staff, never by the engine.
- Open: can tenants request a ward? Does ward size need balancing (each ~150+)? If ward is fixed per apartment and
  apartments have fixed capacity, ward sizes may already be roughly fixed.

## 9. The search, explained simply

The engine builds a plan in steps. Everything below works inside the hard rules; any move that would break one is
simply not considered.

1. **Validate inputs.** Check that locks obey the hard rules, that requests are resolved, and that each plan has
   enough slots. Report shortfalls up front (E15).
2. **Keep locks** exactly as they are.
3. **Seed renewal stays.** Put renewing tenants who want to stay back in their current apartment when the plan
   still matches.
4. **Seed mutual groups.** Place each friend group together in one apartment of their plan, largest groups first;
   groups that don't fit are split as in section 7.
5. **Fill the rest.** Place each remaining tenant where they add the most points (renewal movers first, so they can
   get a nearby apartment).
6. **Improve.** Repeatedly try moving one tenant to another apartment, or swapping two tenants of the same plan, and
   keep the change only if the tiered score gets better. Stop when no move helps or a time limit is reached.

This "seed then improve" approach is fast, simple to explain and easy to step through, but it cannot prove the
result is the best possible plan.

**Alternative: an exact solver** such as Google OR-Tools CP-SAT (open source, runs locally, has a .NET package). The
rules and points are written as a mathematical model, and the solver searches for the best plan, proving optimality
or reporting how far from optimal it might be. Tiers can be handled by solving tier 1, fixing that result, then
solving tier 2, and so on.

| | Seed then improve | Exact solver (CP-SAT) |
|---|---|---|
| Quality | Good, not provably best | Best or with a proven gap |
| Explainability | Each step is traceable | Harder; explanations must be rebuilt after the fact |
| Dependencies | None | One large native library |
| Speed at ~1,100 tenants | Seconds (expected, not measured) | Probably fine because plans split the problem into 10 smaller ones, but not measured |

Either way: **no cloud services or external APIs**. Tenant data never leaves the machine. A reasonable path is to
build seed-then-improve first and use CP-SAT later as a quality check on the same data.

## 10. Deterministic tie-breaking

The same inputs and settings must always give the same draft (proposal):

- Tenants are processed in a fixed order (by tenant code), apartments in a fixed order (by apartment number), and
  moves are tried in a fixed order.
- If two plans have exactly the same score, the engine keeps the first one found in that fixed order.
- If any randomness is ever used for exploration, its seed is stored in `priority_config`, so the run can be repeated.

Ordering by tenant code is fair only for true ties, not for scarce places. **Who stays unplaced when a plan is
oversubscribed** (E15) is a policy decision: contract signing date, renewals first, or staff choice (open question).
Until staff decide, the engine shows the shortfall and the tenants affected and lets staff choose.

## 11. Explanations

Every placement carries a short explanation staff can read and, if wanted, share. Proposed contents:

- **Points breakdown** by line (S1 to S10), including the points the tenant *could* have earned and didn't.
- **Why not better:** the best-scoring alternative apartment for this tenant and the hard rule, lock or trade-off
  that blocked it, for example "apartment 2120 would have kept your roommate request, but a staff restriction
  applies" or "the only same-floor apartment of your plan is full of renewals who are staying".
- Confidential reasons are never shown, only that a staff rule applied.

Example (fictional): *"Placed in 3301 (W-STD-3, floor 1). Renewal stay kept (+95). Mutual request with Parker
Tumblewhump kept (+90). Bedtime: both known roommates in your bucket (+70). Tier 1: 185 of 185."*

## 12. Worked examples from the test data

All tenants are fictional and come from the small seed (`db/seed_small.sql`, seed 2027); the answer key is
`db/seed_small_answer_key.json`. Apartment numbers are from the small seed.

| Case | Situation | How the design handles it |
|---|---|---|
| **E01** group too big | Five W-STD-2 tenants (FAKE-00001 to 00005) all request each other; W-STD-2 holds 4 (apartments 3102, 3227, 3125). | Cannot place all five together (H2). Split 4 + 1: the four each get 3 of 4 mutual partners = 67.5, total 270; the one gets 0. Split 3 + 2: 3 x 45 + 2 x 22.5 = 180. So 4 + 1 wins, and who is left out is only a tie-break, so it goes to the review queue. If one of them had a private room, an apartment would hold only 3 of them (2 + 1 + 1 slots), giving 3 + 2. |
| **E02** animal vs allergy | Cyrus Puddlebeam (FAKE-00006, has an animal) and Vale Wafflebean (FAKE-00007, allergy restriction) request each other, M-STD-3. | The seed has no confirmation fields yet, so Vale's flag counts as **self-reported**: tier 0b keeps them apart (M-STD-3 has spare apartments), the mutual request is not honored, and a "needs staff review" item explains why. Their 2 x 90 mutual points can't outweigh tier 0b. If staff confirm Vale's documentation, H5 makes the separation hard. If Cyrus were a staying renewal, H9 would pin him and Vale would be placed elsewhere. Only a recorded staff override could put them together. |
| **E03** misspelling | Harper Pebblebottom (FAKE-00008) wrote "Quinn Jelyberry"; Quinn Jellyberry (FAKE-00009) wrote Harper's name correctly. W-STD-3. | Suggest FAKE-00009 (one letter off) for staff confirmation. Once confirmed the request becomes mutual (90 each if placed together). Until then it is treated as one-way from Quinn's side only. |
| **E06** ambiguous | Piper Kazoonugget (FAKE-00013) requested just "Jordan"; Jordan Wobblewick (FAKE-00014) and Jordan Moontooth (FAKE-00015) are both in M-FLAT-4. | `ambiguous`; no guessing. Review item lists both Jordans. |
| **E07** cross-building | Indigo Crumpetbeam (FAKE-00016, W-STD-3) requests Lark Jellynugget (FAKE-00017, M-STD-3). | Excluded (H1, different building), shown with the reason. |
| **E08** different plan | FAKE-00018 (M-STD-3) and FAKE-00019 (M-OTH-4) request each other. | Cannot share without a contract change (H1). Flagged "contract change needed?"; the engine never edits contracts. |
| **E09** renewal pair stays | Wren Moontooth (FAKE-00020) and Parker Tumblewhump (FAKE-00021) live in 3301 (W-STD-3, floor 1), want to stay, and request each other. | Seeded first (step 3). Each earns S1 95 + S2 90 = 185 tier-1 points. The other 4 slots in 3301 are filled by others. |
| **E10** renewal move | Kestrel Moonwhump (FAKE-00022) lives in 2421 (M-STD-3, men's floor 1) and wants to move. | Nearby means same floor and same ward. In the small seed the only other M-STD-3 apartment, 2401, is on floor 2, so S3 earns 0 there; the explanation says "no other M-STD-3 apartment on your floor". Whether an adjacent floor gets partial credit is open. (2421 also holds E12's locked tenant.) |
| **E11** plan changed | Kestrel Kazootoes (FAKE-00023) lives in 2401 (M-STD-3, floor 2), wants to stay, but contracted M-FLAT-4. | Staying breaks H1, so S1 is impossible and the case is flagged. Treated as a move: M-FLAT-4 apartment 2120 is on the same floor, so it earns S3 (ward to be confirmed once mapped). |
| **E14** restriction vs request | Logan Tumbletoes (FAKE-00027) requests Dakota Moontoes (FAKE-00026); staff confirmed they must not share. Both M-OTH-4. | H4 wins; the request is excluded with "blocked by a staff restriction". In the small seed M-OTH-4 has only one apartment (2301), so at most one of them can be placed at all. |
| **E15** oversubscribed | M-OTH-4 has 10 contracts and 8 places (one apartment, 2301). | Tier 0: at most 8 placed; at least 2 unplaced, and because of E14 one of those must be FAKE-00026 or FAKE-00027. Nobody is moved to another plan. Staff see the shortfall and choose (section 10). In the large seed it is 91 contracts for 80 places. |

## 13. Fairness

- **Per-tenant points** so large groups and large apartments do not dominate (5.1).
- **Missing optional data is neutral.** Nobody is rewarded or penalized for leaving optional fields blank.
- **Extras are capped low** and never outrank roommate requests or renewals. "Similar" is not assumed to mean
  "better roommates".
- **Renewals get priority** (S1, S3, S10) per Samuel's weights. That is a deliberate advantage over new tenants and
  should be a conscious team decision.
- **No hidden rules.** Every point and every block is visible to staff; confidential reasons are summarized as
  "staff restriction".
- **Distribution, not just totals.** The draft summary should show how many tenants got none of their requests, not
  only the total score. A "worst-off first" objective (maximize the minimum satisfaction) is a possible alternative to
  discuss.
- **Private rooms are paid**, so they reduce capacity for others by design. That is expected, not unfair.

## 14. Proposed schema changes (awaiting team review; NOT applied)

None of these are in `db/schema.sql` yet. They follow the forms' real answer options.

| Change | Purpose | Notes |
|---|---|---|
| `contracts.room_type ENUM('shared','private')` | Bedroom-slot capacity (4.1). | Assumes the private option is on the contract (open question). |
| `contracts.linked_contract_id` (the same tenant's contract for the other semester) | S10 continuity for "Second Semester" signups. | Or a shared `lease_group_id`. |
| `preferences.cleanliness TINYINT NULL CHECK (1-5)` | S8. | New. |
| `preferences.bedtime ENUM('8-9pm','10-11pm','12-1am','after_1am') NULL` | S4. | Replaces the current `sleep_schedule` (`early`/`typical`/`late`/`unknown`), which doesn't match the form. |
| `preferences.social_preference ENUM('shy','outgoing') NULL` | S6a. | Replaces the current three values (`outgoing`/`balanced`/`introverted`). |
| `preferences.academic_stage ENUM('freshman','sophomore','junior','senior') NULL` | S6b. | Replaces the current five values; the form has no graduate option. |
| `preferences.age`, `preferences.major` | S6c, S6d. | Already exist; no change. |
| `ward_requests` table (contract, `raw_text`, `resolved_ward_id`, status, reason), like `roommate_requests` | S9. | Raw text kept only until staff review it (privacy doc D5). |
| `wards` table + `apartments.ward_id` (nullable) | S3 nearby, S9, ward on each placement. | Null until staff give the mapping. |
| `apartments.is_accessible BOOLEAN DEFAULT FALSE` | H6 and S7. | |
| New `placement_restrictions` type `requires_accessible` | Staff-confirmed accessibility need (H6). | Reuses the confirmed_by / confirmed_at audit fields. |
| Replace `preferences.animal_allergy_restriction` with `allergy_self_reported` (Yes on either form) plus `allergy_confirmed_by` / `allergy_confirmed_at` | 4.3, H5, tier 0b. | Records only that documentation was confirmed, never its content. |
| `has_animal` gains `animal_approved_by` / `animal_approved_at` | H5, H9. | Staff-entered from the Reasonable Accommodation Process. |
| New `placement_restrictions` type `allergy_override` (pair) | Staff override of H5 for a specific pair. | Recorded with who confirmed it and when. |
| `capacity_override` in slots instead of headcount (or add a slot override) | Express "one bedroom unavailable" (E16). | Open question. |
| `priority_config` JSON layout | Store weights, tiers, caps and any random seed per draft. | Column exists; the layout is new. |
| **Not added:** gender, birthday, emails, address, phone, emergency contact, parking, comments, marketing source, waitlist | | See [data-privacy.md](data-privacy.md). |

The fictional seeds still use the current columns; they will be updated together with the schema once the team
approves these changes.

## 15. Open questions for Lodge staff

1. **Private rooms:** is the private option written on the contract? Is the price different? Can it change mid-term?
2. **Odd numbers:** may a shared-rate tenant end up alone in a bedroom?
3. **Capacity override:** should it be in people or in bedrooms?
4. **Apartment counts:** how many apartments of each plan, on which floors? (The test data uses placeholders.)
5. **Wards:** is each apartment's ward really fixed across semesters? Can we get the apartment-to-ward list? Can
   tenants request a ward? Do ward sizes need balancing?
6. **Nearby:** same floor and same ward is full credit. Does an adjacent floor, or the same ward on another floor,
   count for anything?
7. **Renewal "move":** if a tenant asks to move, is putting them back in the same apartment forbidden or just
   unrewarded?
8. **Allergies:** do they ever matter between neighbouring apartments, or only inside one apartment? Who may approve
   an override? How will staff mark documentation as confirmed (who, in which screen), and what happens if it
   arrives after a draft or after assignments are final? Is a Yes without documentation treated as anything today?
9. **Accessible apartments:** must they stay empty for possible late applicants, or may others use them when no one
    needs them? Who confirms an accessibility need?
10. **Oversubscription:** when a plan has more contracts than places, who is left unplaced, and what happens to them?
11. **Groups too big:** is 4 + 1 acceptable, or would staff rather split more evenly? Should the separated person stay
    on the same floor as the group?
12. **Extras:** for age, introvert/outgoing, major and academic stage, does "match" mean similar? Should tenants say
    whether they want similar roommates?
13. **Terms:** is a tenant returning in Fall after Spring a "renewal"? Are Spring contracts different in any way?
14. **Second semester signups:** does the Lodge promise the same apartment for both semesters, or only the discount?
15. **Animals:** can staff give us a yes/no list of approved animals from the Reasonable Accommodation Process each
    term? Are any animals other than approved assistance animals/ESAs allowed? Does "will not move the ESA tenant
    out" also cover renewals across terms (our reading, H9), and may an approved holder still choose to move?
16. **Ward requests:** how often are they honored today, and are there wards students may not request?
17. **Both roommate fields:** if Form 1 and Form 2 name different people, which one counts?
18. **Cleanliness and bedtime:** do staff already use these answers when matching by hand, and how much?

## 16. Review questions for other AI tools

Paste this document and ask:

1. Is per-tenant scoring (rather than per pair) the right unit? What does it get wrong for groups and chains of
   requests?
2. Do the tiers in 5.4 match the stated weights, or do they distort them? Would you merge or split any tier?
3. Give a concrete scenario where the tiered score produces a result the manager would clearly dislike.
4. Is "hard rule after confirmed documentation, tier 0b before" the right model for a .98 allergy weight and the Lodge's ESA policy?
5. Is the bedroom-slot capacity model correct and complete? What cases does it miss?
6. What is the risk that seed-then-improve gets stuck in a poor plan, and what cheap fixes exist?
7. Is the E01 outcome (4 + 1 split) reasonable? What scoring change would favor 3 + 2, and should it?
8. Is the sleep compatibility scale (1 / 0.5 / 0) sensible? What about tenants with "unknown"?
9. What fairness problems does this design have that it does not mention?
10. What should a staff-facing explanation always include, and what must it never reveal?
11. For about 1,100 tenants and 200 apartments, is an exact solver (CP-SAT) clearly worth it from the start?
12. Is the revised weight table in 5.1 reasonable? Should cleanliness be above bedtime, or even in tier 1?
13. Is "half credit for one step apart" right for a 1-5 cleanliness scale, or should a 1 next to a 5 be penalized?
14. Should two-semester continuity (S10) be a hard rule?
15. Which items in the decision summary (section 0) marked "Recommended default" should really be team decisions?
