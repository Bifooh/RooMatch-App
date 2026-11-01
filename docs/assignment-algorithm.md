# Roomatch: how tenants get assigned to apartments (DRAFT design)

**Status:** draft for team review, 2026-10-31. Nothing in this document is implemented yet. It describes *what*
the assignment engine should decide and *why*, in plain words, so it can be critiqued (including by other AI tools)
before anyone writes solver code.

**How to read the labels**

| Label | Meaning |
|---|---|
| **(Samuel/manager input)** | A fact or priority given by Samuel, sometimes relaying the Lodge manager. |
| **(proposal)** | Our suggested design. Open to change. |
| **(open question)** | Unknown. Must be answered by Lodge staff before it is relied on. Collected in section 15. |

All names and tenant codes in the examples are **fictional** (they come from the repo's generated test data).

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
| H5 | **Animal vs allergy.** A tenant with an animal-allergy restriction may not share an apartment with a tenant who has an animal. | Samuel/manager input: weight .98, "people are very passionate about it" | Treated as **hard unless staff explicitly override it for that pair** (see 5.3). Whether neighbouring apartments matter is an open question. |
| H6 | **Accessibility need.** A tenant with a **staff-confirmed** accessibility need must get a handicap-adapted apartment of their plan. | Samuel/manager input: a few first-floor apartments are adapted; rule is a proposal | If their plan has no free adapted apartment, they go to the review queue. The opposite direction (others avoiding these apartments) is a soft rule, S7. |
| H7 | **Manager locks.** A locked assignment is kept exactly as is. | Existing schema (`is_locked`, `locked_by`) | If a lock itself breaks H1 to H6, the engine refuses to build the draft and shows the conflict; it does not move the lock. |
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
| Men's Standard 3bd/3ba (M-STD-3) | 3 | **6 (assumed)** | Left blank by Samuel; **open question** |
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

## 5. The points score

### 5.1 Samuel's weights

Samuel gave importance weights on a 0 to 1 scale (Samuel/manager input). We turn each into **points = weight x 100**,
earned **per tenant**, at full satisfaction (proposal).

| Code | Preference | Weight | Max points per tenant | How a tenant earns them (proposal) |
|---|---|---|---|---|
| (H5) | Avoid animal/allergy conflicts | .98 | n/a, it is a hard rule | See 5.3 for why it is not scored. |
| S1 | Renewing tenant who wants to **stay** stays in their current apartment | .95 | 95 | All or nothing. Only possible if the current apartment's plan matches the new contract (E11 is the counter-example). |
| S2 | **Mutual** roommate requests honored | .90 | 90 | 90 x (mutual partners placed with them / mutual partners they requested). |
| S3 | Renewal moves stay **nearby** (= same floor AND same ward) | .85 | 85 | For a renewing tenant who is not staying (wants to move, or cannot stay): 85 if the new apartment is on the same floor and in the same ward as the old one. While ward data is missing, same floor counts as nearby. Partial credit for an adjacent floor is an open question. |
| S4 | Sleep schedules match | .70 | 70 | 70 x average compatibility with roommates whose schedule is known: same = 1, one step apart (early/typical, typical/late) = 0.5, early/late = 0. Tenants with "unknown" are left out of the average. |
| S5 | **One-way** roommate requests honored | .55 | 55 | 55 x (one-way requested people placed with them / one-way requested people). Points go to the requester only. |
| S6 | Optional extras: age, introvert/outgoing, major, academic stage | .30 | 30 total (7.5 each) | Only counted where both people gave a value. Whether "match" means *similar* is an open question (section 15, question 13). |
| S7 | Non-needing tenants avoid filling accessible apartments | none given; we propose .60 | -60 per tenant | A penalty for each tenant without an accessibility need placed in an `is_accessible` apartment, so those apartments stay free when that is avoidable. |

Per tenant rather than per pair, so a big apartment or a big friend group does not collect more points just by having
more pairs. A full mutual group of 4 earns 4 x 90 = 360 (four happy people), not 6 pairs x 90 = 540.

Missing data never earns or loses points (it just leaves that line out), so optional fields stay optional.

### 5.2 The danger: many small points beating one big one

A plain sum lets many low-weight points outvote one high-weight point. Example: a renewing tenant who wants to stay
earns S1 = 95. Suppose moving her out would let five other people in that apartment improve their sleep match from
0.5 to 1.0: each gains 70 x 0.5 = 35, total 175. A plain sum says "move the renewal", which is surely not what the
manager meant by .95 vs .70. Extras (S6) are even more dangerous because nearly everyone has a few of them.

### 5.3 Hard rule vs a .98 weight

A weight, however high, can be outbid. At 98 points, an allergy conflict would be "worth it" if it unlocked two
mutual pairs (4 x 90 = 360 points). That is why H5 is a **hard rule with an explicit staff override** instead of a
98-point score: the system never trades it away; only a named staff member can, for one specific pair, and that
override is recorded (proposed schema change in section 14). The .98 tells us how strongly the manager feels; the
override is how "98% not 100%" is expressed safely.

### 5.4 Proposal: tiers, with points inside each tier

| Tier | Contents | Compared how |
|---|---|---|
| 0 | Number of contracted tenants placed | First. Always place as many people as the hard rules allow. |
| 1 | S1 renewal stay (95), S2 mutual requests (90), S3 nearby renewal moves (85) | Points added up inside the tier. |
| 2 | S4 sleep (70), S7 accessible-apartment penalty (-60), S5 one-way requests (55) | Only used to choose between plans that tie on tiers 0 and 1. |
| 3 | S6 extras (30) | Only used to break ties on tiers 0 to 2. |

Plans are compared **tier by tier** (lexicographic): a plan with more tier-1 points always wins, however many tier-2
or tier-3 points the other one has. Inside a tier, Samuel's weights decide the trade-offs (one renewal stay, 95, beats
one tenant's mutual request, 90).

**Tradeoffs**

| Option | Pros | Cons |
|---|---|---|
| **Tiers (recommended)** | Guarantees the 5.2 problem cannot happen across tiers. Easy to explain ("we kept your renewal; sleep only breaks ties"). | Rigid: 1 tier-1 point beats 1,000 tier-2 points, so a tiny gain for one person can cost many people their sleep match. The tier boundaries become a new decision for the team. |
| **Single sum with per-tenant caps** | One number; trades off smoothly. Caps (each tenant can earn at most 70 from S4, 55 from S5, 30 from S6) limit the damage. | Does not remove the problem: one tenant's 95 can still lose to several other tenants' capped points (the example in 5.2 still flips). |
| **Single sum with steep weights** (e.g. tier 1 x 1,000, tier 2 x 10) | Behaves like tiers but still one number; easy for an exact solver. | The multipliers are arbitrary and must be large enough for the dataset size; hard to explain. |

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
| Blocked by a hard rule (must_not_share, animal/allergy) | E14, E02 | `excluded` with the rule as reason. | Review item; the confidential reason is not shown. |

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

- **Points breakdown** by line (S1 to S7), including the points the tenant *could* have earned and didn't.
- **Why not better:** the best-scoring alternative apartment for this tenant and the hard rule, lock or trade-off
  that blocked it, for example "apartment 2620 would have kept your roommate request, but a staff restriction
  applies" or "the only same-floor apartment of your plan is full of renewals who are staying".
- Confidential reasons are never shown, only that a staff rule applied.

Example (fictional): *"Placed in 3616 (W-STD-3, floor 1). Renewal stay kept (+95). Mutual request with Parker
Tumblewhump kept (+90). Sleep: 2 of 2 known roommates match (+70). Tier 1: 185 of 185."*

## 12. Worked examples from the test data

All tenants are fictional and come from the small seed (`db/seed_small.sql`, seed 2027); the answer key is
`db/seed_small_answer_key.json`. Apartment numbers are from the small seed.

| Case | Situation | How the design handles it |
|---|---|---|
| **E01** group too big | Five W-STD-2 tenants (FAKE-00001 to 00005) all request each other; W-STD-2 holds 4 (apartments 5555, 5842, 8314). | Cannot place all five together (H2). Split 4 + 1: the four each get 3 of 4 mutual partners = 67.5, total 270; the one gets 0. Split 3 + 2: 3 x 45 + 2 x 22.5 = 180. So 4 + 1 wins, and who is left out is only a tie-break, so it goes to the review queue. If one of them had a private room, an apartment would hold only 3 of them (2 + 1 + 1 slots), giving 3 + 2. |
| **E02** animal vs allergy | Cyrus Puddlebeam (FAKE-00006, has an animal) and Vale Wafflebean (FAKE-00007, allergy restriction) request each other, M-STD-3. | H5 forbids sharing. The request is excluded with the reason; they go to different M-STD-3 apartments. Losing 2 x 90 mutual points does not matter: a hard rule is never traded. Only a recorded staff override could allow it. |
| **E03** misspelling | Harper Pebblebottom (FAKE-00008) wrote "Quinn Jelyberry"; Quinn Jellyberry (FAKE-00009) wrote Harper's name correctly. W-STD-3. | Suggest FAKE-00009 (one letter off) for staff confirmation. Once confirmed the request becomes mutual (90 each if placed together). Until then it is treated as one-way from Quinn's side only. |
| **E06** ambiguous | Piper Kazoonugget (FAKE-00013) requested just "Jordan"; Jordan Wobblewick (FAKE-00014) and Jordan Moontooth (FAKE-00015) are both in M-FLAT-4. | `ambiguous`; no guessing. Review item lists both Jordans. |
| **E07** cross-building | Indigo Crumpetbeam (FAKE-00016, W-STD-3) requests Lark Jellynugget (FAKE-00017, M-STD-3). | Excluded (H1, different building), shown with the reason. |
| **E08** different plan | FAKE-00018 (M-STD-3) and FAKE-00019 (M-OTH-4) request each other. | Cannot share without a contract change (H1). Flagged "contract change needed?"; the engine never edits contracts. |
| **E09** renewal pair stays | Wren Moontooth (FAKE-00020) and Parker Tumblewhump (FAKE-00021) live in 3616 (W-STD-3, floor 1), want to stay, and request each other. | Seeded first (step 3). Each earns S1 95 + S2 90 = 185 tier-1 points. The other 4 slots in 3616 are filled by others. |
| **E10** renewal move | Kestrel Moonwhump (FAKE-00022) lives in 5793 (M-STD-3, men's floor 1) and wants to move. | Nearby means same floor and same ward. In the small seed the only other M-STD-3 apartment, 7679, is on floor 2, so S3 earns 0 there; the explanation says "no other M-STD-3 apartment on your floor". Whether an adjacent floor gets partial credit is open. (5793 also holds E12's locked tenant.) |
| **E11** plan changed | Kestrel Kazootoes (FAKE-00023) lives in 7679 (M-STD-3, floor 2), wants to stay, but contracted M-FLAT-4. | Staying breaks H1, so S1 is impossible and the case is flagged. Treated as a move: M-FLAT-4 apartment 2620 is on the same floor, so it earns S3 (ward to be confirmed once mapped). |
| **E14** restriction vs request | Logan Tumbletoes (FAKE-00027) requests Dakota Moontoes (FAKE-00026); staff confirmed they must not share. Both M-OTH-4. | H4 wins; the request is excluded with "blocked by a staff restriction". In the small seed M-OTH-4 has only one apartment (7215), so at most one of them can be placed at all. |
| **E15** oversubscribed | M-OTH-4 has 10 contracts and 8 places (one apartment, 7215). | Tier 0: at most 8 placed; at least 2 unplaced, and because of E14 one of those must be FAKE-00026 or FAKE-00027. Nobody is moved to another plan. Staff see the shortfall and choose (section 10). In the large seed it is 91 contracts for 80 places. |

## 13. Fairness

- **Per-tenant points** so large groups and large apartments do not dominate (5.1).
- **Missing optional data is neutral.** Nobody is rewarded or penalized for leaving optional fields blank.
- **Extras are capped low** and never outrank roommate requests or renewals. "Similar" is not assumed to mean
  "better roommates".
- **Renewals get priority** (S1, S3) per Samuel's weights. That is a deliberate advantage over new tenants and
  should be a conscious team decision.
- **No hidden rules.** Every point and every block is visible to staff; confidential reasons are summarized as
  "staff restriction".
- **Distribution, not just totals.** The draft summary should show how many tenants got none of their requests, not
  only the total score. A "worst-off first" objective (maximize the minimum satisfaction) is a possible alternative to
  discuss.
- **Private rooms are paid**, so they reduce capacity for others by design. That is expected, not unfair.

## 14. Proposed schema changes (awaiting team review; NOT added yet)

| Change | Purpose | Notes |
|---|---|---|
| `contracts.room_type ENUM('shared','private')` (or `is_private_room`) | Bedroom-slot capacity (4.1). | Assumes the private option is on the contract (open question). |
| `wards` table + `apartments.ward_id` (nullable) | S3 nearby, ward on each placement. | Null until staff give the mapping. |
| `apartments.is_accessible BOOLEAN DEFAULT FALSE` | H6 and S7. | |
| New `placement_restrictions` type `requires_accessible` | Staff-confirmed accessibility need (H6). | Reuses the existing confirmed_by / confirmed_at audit fields. |
| New `placement_restrictions` type `allergy_override` (pair) | Staff override of H5 for a specific pair. | Recorded with who confirmed it and when. |
| `capacity_override` in slots instead of headcount (or add a slot override) | Express "one bedroom unavailable" (E16). | Open question. |
| Sleep schedule | S4. | `preferences.sleep_schedule` **already exists** (`early`/`typical`/`late`/`unknown`). Confirm these categories match what the Lodge collects. |
| `priority_config` JSON layout | Store weights, tiers, caps and any random seed per draft. | Column exists; the layout is new. |

## 15. Open questions for Lodge staff

1. **Men's Standard 3bd/3ba capacity:** 6 (2 per bedroom) is assumed. Correct?
2. **Private rooms:** is the private option written on the contract? Is the price different? Can it change mid-term?
3. **Odd numbers:** may a shared-rate tenant end up alone in a bedroom?
4. **Capacity override:** should it be in people or in bedrooms?
5. **Apartment counts:** how many apartments of each plan, on which floors? (The test data uses placeholders.)
6. **Wards:** is each apartment's ward really fixed across semesters? Can we get the apartment-to-ward list? Can
   tenants request a ward? Do ward sizes need balancing?
7. **Nearby:** same floor and same ward is full credit. Does an adjacent floor, or the same ward on another floor,
   count for anything?
8. **Renewal "move":** if a tenant asks to move, is putting them back in the same apartment forbidden or just
   unrewarded?
9. **Allergies:** do they ever matter between neighbouring apartments, or only inside one apartment? Who may approve
   an override?
10. **Accessible apartments:** must they stay empty for possible late applicants, or may others use them when no one
    needs them? Who confirms an accessibility need?
11. **Oversubscription:** when a plan has more contracts than places, who is left unplaced, and what happens to them?
12. **Groups too big:** is 4 + 1 acceptable, or would staff rather split more evenly? Should the separated person stay
    on the same floor as the group?
13. **Extras:** for age, introvert/outgoing, major and academic stage, does "match" mean similar? Should tenants say
    whether they want similar roommates?
14. **Terms:** is a tenant returning in Fall after Spring a "renewal"? Are Spring contracts different in any way?

## 16. Review questions for other AI tools

Paste this document and ask:

1. Is per-tenant scoring (rather than per pair) the right unit? What does it get wrong for groups and chains of
   requests?
2. Do the tiers in 5.4 match the stated weights, or do they distort them? Would you merge or split any tier?
3. Give a concrete scenario where the tiered score produces a result the manager would clearly dislike.
4. Is "hard rule with staff override" the right model for a .98 allergy weight?
5. Is the bedroom-slot capacity model correct and complete? What cases does it miss?
6. What is the risk that seed-then-improve gets stuck in a poor plan, and what cheap fixes exist?
7. Is the E01 outcome (4 + 1 split) reasonable? What scoring change would favor 3 + 2, and should it?
8. Is the sleep compatibility scale (1 / 0.5 / 0) sensible? What about tenants with "unknown"?
9. What fairness problems does this design have that it does not mention?
10. What should a staff-facing explanation always include, and what must it never reveal?
11. For about 1,100 tenants and 200 apartments, is an exact solver (CP-SAT) clearly worth it from the start?
