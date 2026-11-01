# Lodge tenant-assignment MVP

**All tenant data here is FICTIONAL.** No real resident data was used, and none should go into these files. The floor plan names, the two buildings and the four floors are real Lodge facts from Samuel. Resident capacities are explicit per-plan values from Samuel/manager input (2 per bedroom by default); all 10 plans are confirmed.
Real-data deployment needs its own security and operations review.

This repo holds the MySQL schema and a reproducible fictional dataset (deliverable 1), plus the app skeleton:
a Vite + React + TypeScript frontend and an ASP.NET Core (.NET 8) minimal API. The rules engine and solver come later;
their plain-English draft design is **[docs/assignment-algorithm.md](docs/assignment-algorithm.md)** (hard rules, the
points score built from Samuel's weights, worked examples and open questions). How student data must be handled is
in **[docs/data-privacy.md](docs/data-privacy.md)**.
Everything runs locally: no cloud services, telemetry or external APIs.

## Contents

| Path | What it is |
|---|---|
| `docs/assignment-algorithm.md` | DRAFT design of the assignment engine, in plain English, for team and AI review. No code yet. |
| `docs/data-privacy.md` | DRAFT privacy and security plan: field-by-field table for the Lodge's two forms, what is never stored, roles, encryption, retention, import, logging, questions for the Lodge. |
| `db/schema.sql` | MySQL 8 schema: 11 tables plus the `v_apartment_capacity` view. Re-running it drops and recreates the tables. |
| `db/seed_small.sql` | 32 tenants and 16 apartments (capacity 99), seed 2027. It needs at least 32 tenants to fit every edge case. |
| `db/seed_large.sql` | 1,100 tenants and 202 apartments (capacity 1,253), seed 2027. |
| `db/seed_*_answer_key.json` | The deliberate edge cases (tenant codes, apartments, expected handling) plus the random noise added to the large set. Use these in tests. |
| `db/sanity_checks.sql` | Read-only checks: counts, demand vs capacity per floor plan, renewal plan mismatches, and requests with no exact name match. |
| `tools/DataGen/` | The generator: a C# (.NET 8) console app with no NuGet dependencies. |
| `Lodge.sln` | Solution covering `Lodge.Api`, `Lodge.Tests` and `DataGen`. |
| `backend/Lodge.Api/` | ASP.NET Core minimal API (MySqlConnector). `GET /api/health`, `GET /api/summary`. |
| `backend/Lodge.Tests/` | xUnit test project (one smoke test for now; the rules engine tests go here). |
| `frontend/` | Vite + React + TypeScript app. Dev server proxies `/api` to the API. |

## Run it locally

Prerequisites: .NET 8 SDK, Node 20+ (verified with Node 24), MySQL 8.

### 1. Database

The dev box runs a user-space MySQL 8.0.40 on a Unix socket only (`--skip-networking`), socket
`~/mysql/run/mysql.sock`, user `root` with no password, databases `lodge_small` and `lodge_large`.

```bash
source ~/mysql/env.sh        # sets LD_LIBRARY_PATH and $MYSQL
$MYSQL -e "SELECT COUNT(*) FROM lodge_small.tenants"   # expect 32
```

To (re)create a database, follow "Load into MySQL" below.

### 2. API

```bash
cp backend/Lodge.Api/appsettings.Local.example.json backend/Lodge.Api/appsettings.Local.json
# edit the connection string: socket path, database (lodge_small / lodge_large), password (empty on the dev box)
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build Lodge.sln && dotnet test Lodge.sln
cd backend/Lodge.Api && dotnet run --launch-profile http     # http://localhost:5080
curl localhost:5080/api/health    # {"status":"ok","database":"lodge_small","serverVersion":"8.0.40"}
curl localhost:5080/api/summary   # {"tenants":32,"apartments":16,"floorPlans":10}
```

`appsettings.Local.json` is git-ignored and overrides `appsettings*.json`. You can also use
`ConnectionStrings__Lodge=...` as an environment variable; it takes precedence over the local file
(for example, point it at `lodge_large`). Without either, the Development default
(TCP `localhost:3306`, root, empty password) is used, which won't work against the socket-only dev server.

### 3. Frontend

```bash
cd frontend
npm install
npm run dev      # http://localhost:5173, proxies /api to http://localhost:5080 (override with LODGE_API_URL)
npm run build    # type-check + production build into dist/
```

## Decisions

- **Monorepo, one solution.** `Lodge.sln` at the root covers `backend/Lodge.Api`, `backend/Lodge.Tests` and the existing `tools/DataGen` (left unchanged).
- **Minimal API + MySqlConnector, no ORM.** Raw SQL through `MySqlDataSource` (registered with `AddMySqlDataSource`). Dapper can be added later if mapping gets tedious; EF Core was rejected as too heavy for a schema we already own in `db/schema.sql`.
- **Connection strings:** no secrets committed. The committed `appsettings.Development.json` has a password-less TCP default; real local values go in the git-ignored `appsettings.Local.json` (template: `appsettings.Local.example.json`) or env vars. Chosen over `dotnet user-secrets` so the setting lives next to the project and is easy to see.
- **Unix socket** for the dev DB, because the box's MySQL runs with `--skip-networking`.
- **Health endpoint** opens a connection and runs `SELECT DATABASE(), VERSION()`; returns 503 with the error message if the DB is unreachable.
- **HTTP only on port 5080** in development (launch profile trimmed; no HTTPS profile, no OpenAPI/Swagger package) to keep it small. Revisit HTTPS/auth before any real data.
- **Vite dev proxy** for `/api`, so no CORS configuration is needed in the API. Production hosting is undecided.
- **Frontend:** stock `create-vite` react-ts template (Vite 8, React, oxlint), no UI library or state library yet.
- **Tests:** xUnit project with one smoke test; it doesn't reference the API yet. The rules engine will likely be its own class library referenced by both API and tests.
- **Telemetry:** set `DOTNET_CLI_TELEMETRY_OPTOUT=1` when running the .NET CLI.
- **Capacity (2026-10-31, "LODGE UPDATE"):** Samuel/manager input gives every bedroom 2 people by default: 4/4/6/8/8/8 for the women's plans and 8 for men's Flat, Standard Townhome and Oversized Townhome. Men's Standard 3bd (M-STD-3) was first left blank, then confirmed as 6 by Samuel/manager the same day, so all 10 plans have `capacity_is_assumption = FALSE`. Values stay explicit per plan (not a bedrooms x 2 formula).
- **Private rooms:** some students pay extra for a bedroom + bathroom to themselves, so an apartment's real headcount varies (e.g. 3 people in a 2-bedroom). The design doc proposes counting capacity in bedroom slots (shared = 1, private = 2, apartment = 2 x bedrooms). Not in the schema or seeds yet.
- **Terms:** the Lodge has only Fall, Winter and Spring terms (no Summer). The seeds contain F2026 and W2027.
- **Wards:** everyone is LDS; the Lodge is one stake of about 6 wards (150+ people each). Each apartment's ward is believed to be fixed across semesters (to confirm), so a tenant's ward follows from placement. A floor can hold parts of several wards and a ward spans both buildings. `apartments.ward_id` is proposed, not added.
- **Accessible apartments:** a few first-floor apartments are handicap-adapted. `apartments.is_accessible` and a staff-confirmed accessibility need are proposed, not added.
- **Priorities:** Samuel gave weights (allergy .98 treated as a hard rule, renewal stay .95, mutual requests .90, nearby renewal moves .85, sleep .70, one-way requests .55, extras .30). How they become points is in the design doc.
- **Lodge forms (2026-10-31):** Samuel shared the field lists (no values) of the website application (Form 1) and the roommate-matching form (Form 2). The weights were revised from them (cleanliness, bedtime buckets, ward requests, two-semester continuity); Samuel's originals are kept next to the recommendations in the design doc. Gender, contact details, birthday, parking, comments and marketing source are never stored.
- **Proposed schema changes are deliberately not applied yet** (private room, ward, accessibility, allergy override). They are listed in the design doc for team review.
- **Edge cases after the capacity change:** M-OTH-4 now holds 8, so oversubscribing it (E15) needs at least 9 contracts there. To keep the small set at exactly 32 tenants, E05 (non-mutual pair) and E14 (must-not-share pair) moved from M-STH-4 and M-FLAT-4 into M-OTH-4; their meaning is unchanged. E01 (5 in W-STD-2, cap 4) and E16 (override 5 < plan 6) still hold as-is.
- **Spare capacity rule unchanged:** per plan, the apartments needed plus max(1, ceil(10%)). With the larger capacities this gives 13.9% overall spare in the large set (more than 10% because of per-plan rounding).
- **Config precedence:** environment variables override `appsettings.Local.json`.
- **Git author:** the first two commits use the placeholder `Samuel Pereira <samuel@localhost>`; later commits use Samuel's real address (repo-local `git config`). History was not rewritten.

## Design rules the schema enforces

- **Identity is separate from term data.** `tenants` holds only the tenant code and name. `contracts`, `preferences`, `roommate_requests`, `placement_restrictions` and `assignments` are all per term or per contract.
- **Apartment numbers only.** There are no room or bed identifiers anywhere. Numbers are 4 digits (like 2420) and don't encode the floor, so `floor` is stored explicitly. Seeds use random, unique 4-digit numbers.
- **Two buildings, Women's and Men's, with 4 floors each.** In the seeds, each plan's apartments are spread across floors 1 to 4 in turn.
- **Capacity is stored explicitly** in `floor_plans.resident_capacity`, never derived from bedrooms. A staff-confirmed `apartments.capacity_override` can replace it. `v_apartment_capacity.effective_capacity` is the number validation should use.
- **Renewal fields are consistent.** A CHECK constraint makes a `new` contract have no current apartment or stay preference, and a `renewing` contract have both.
- **Assignments stay within one term.** Composite foreign keys mean an assignment's draft and contract must belong to the same term. Each contract gets at most one apartment per draft.
- **Locks need an owner.** A locked assignment must record `locked_by`.
- **Roommate requests keep the original wording** in `raw_text`. Seeds load them as `status='pending'` and unresolved, because resolving names is the app's job. Statuses `unresolved`, `ambiguous` and `excluded` require an `exclusion_reason`.
- **Placement restrictions are staff-confirmed.** Each one records who confirmed it and when, and stores the rule without the confidential reason behind it.
- **Capacity, eligibility and restriction checks are not done by triggers.** They belong in the shared C# rules engine, so the UI can explain why a move fails.

## Regenerate the data

Requires the .NET 8 SDK.

```bash
cd tools/DataGen
dotnet run -c Release -- --size small --seed 2027 --out ../../db
dotnet run -c Release -- --size large --seed 2027 --out ../../db
# any size or seed (must be >= 32 to fit the edge cases):
dotnet run -c Release -- --size 250 --seed 7 --out /tmp/lodge --name custom
```

Output is deterministic. The generator uses its own SplitMix64 random number generator and writes no timestamps, so the same seed and size give byte-identical files. We checked this by running it twice and comparing sha256 hashes.

## Load into MySQL

```bash
mysql -e "CREATE DATABASE lodge_mvp CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"
mysql lodge_mvp < db/schema.sql
mysql lodge_mvp < db/seed_small.sql     # or seed_large.sql (load only one per database)
mysql -t lodge_mvp < db/sanity_checks.sql
```

Verified on MySQL Community Server 8.0.40: the schema and both seeds load cleanly.

## Floor plans (real names; capacity from Samuel/manager input)

| Code | Plan (exact name) | Variant | Resident capacity | Status |
|---|---|---|---|---|
| W-STD-2 | Women's - Standard. 2 Bedroom, 2 Bath | standard | 4 | manager input |
| W-OVR-2 | Women's - Oversized. 2 Bedroom, 2 Bath | oversized | 4 | manager input |
| W-STD-3 | Women's - Standard. 3 Bedroom, 3 Bath | standard | 6 | manager input |
| W-FLAT-4 | Women's - Flat. 4 Bedroom, 4 Bath | flat | 8 | manager input |
| W-STH-4 | Women's - Standard Townhome. 4 Bedroom, 4 Bath | standard_townhome | 8 | manager input |
| W-OTH-4 | Women's - Oversized Townhome. 4 Bedroom, 4 Bath | oversized_townhome | 8 | manager input |
| M-STD-3 | Men's - Standard. 3 Bedroom, 3 Bath | standard | 6 | confirmed by Samuel/manager |
| M-FLAT-4 | Men's - Flat. 4 Bedroom, 4 Bath | flat | 8 | manager input |
| M-STH-4 | Men's - Standard Townhome. 4 Bedroom, 4 Bath | standard_townhome | 8 | manager input |
| M-OTH-4 | Men's - Oversized Townhome. 4 Bedroom, 4 Bath | oversized_townhome | 8 | manager input |

The men's building has no 2-bedroom plans. Capacities come from Samuel/manager input (2026-10-31): every bedroom
holds 2 by default. They are hard-coded per plan in `tools/DataGen/Program.cs` and stored explicitly in
`floor_plans.resident_capacity`; they are never computed from bedrooms. All 10 plans have `capacity_is_assumption = FALSE`
(M-STD-3 = 6 confirmed by Samuel/manager 2026-10-31). These are default
maximums: private rooms (not modelled yet) lower the real headcount.

### Apartment counts per plan (seed 2027)

Each plan gets enough apartments for its demand plus a 10% margin (at least one spare apartment); M-OTH-4 is
deliberately short (E15). Every plan has at least one apartment, so the small set has a lot of spare room.

| Code | Small: apts / capacity / contracts | Large: apts / capacity / contracts |
|---|---|---|
| W-STD-2 | 3 / 12 / 5 | 37 / 148 / 129 |
| W-OVR-2 | 2 / 8 / 2 | 18 / 72 / 62 |
| W-STD-3 | 2 / 11 / 5 | 30 / 179 / 160 |
| W-FLAT-4 | 1 / 8 / 0 | 14 / 112 / 89 |
| W-STH-4 | 1 / 8 / 0 | 11 / 88 / 73 |
| W-OTH-4 | 1 / 8 / 0 | 6 / 48 / 40 |
| M-STD-3 | 2 / 12 / 6 | 41 / 246 / 217 |
| M-FLAT-4 | 2 / 16 / 4 | 19 / 152 / 130 |
| M-STH-4 | 1 / 8 / 0 | 16 / 128 / 109 |
| M-OTH-4 | 1 / 8 / 10 | 10 / 80 / 91 |
| **Total** | **16 / 99 / 32** | **202 / 1,253 / 1,100** (13.9% spare) |

W-STD-3 capacity is one less than apartments x 6 because of the E16 override.

## Deliberate edge cases (the same set in small and large)

| ID | Case |
|---|---|
| E01 | A group of 5 who all request each other, in W-STD-2 (holds 4). |
| E02 | A mutual request where one tenant has an animal and the other has an animal-allergy restriction (M-STD-3). |
| E03 | A misspelled roommate name (W-STD-3; the reverse request is spelled correctly). |
| E04 | A request naming a person who doesn't exist ("Rowan Glimmerpatch"). |
| E05 | A non-mutual request (M-OTH-4; was M-STH-4, moved so E15 fits in 32 tenants). |
| E06 | An ambiguous request ("Jordan") with two Jordans in M-FLAT-4. |
| E07 | A cross-category request: a W-STD-3 tenant names an M-STD-3 tenant (different building). |
| E08 | A mutual request between M-STD-3 and M-OTH-4 (different contracted plans). |
| E09 | Two renewing tenants who want to stay together in their W-STD-3 apartment. |
| E10 | A renewing M-STD-3 tenant who wants to move (prefer the same floor and same ward; adjacent floor is an unconfirmed fallback). |
| E11 | A renewing tenant who wants to stay, but lives in M-STD-3 and contracted M-FLAT-4. |
| E12 | A staff-confirmed `required_floor = 1`, with a manager-locked pre-placement on floor 1. |
| E13 | A manager-locked renewal stay (W-OVR-2). |
| E14 | A staff-confirmed `must_not_share_with` pair in M-OTH-4 (was M-FLAT-4, moved for the same reason), where one of them requested the other. |
| E15 | One oversubscribed plan, **M-OTH-4**: 10 contracts for 8 places (1 apartment) in small, 91 for 80 (10 apartments) in large. Total capacity is still enough overall (99 for 32 tenants in small, 1,253 for 1,100 in large). |
| E16 | One W-STD-3 apartment with `capacity_override = 5` (plan capacity 6). |

The 2 locked assignments live in draft 1, "Manager pre-placements (fictional)".

The **large** set also adds random noise, all listed in its answer key:
- About 3% of tenants have an animal and about 4% have an allergy restriction.
- 190 roommate requests are non-mutual because the reverse request was dropped.
- 43 roommate names are randomly misspelled.
- 4 requests name people who don't exist.
- About a third of tenants are renewals.

The **small** set has no random animals or allergies, so every conflict in it is deliberate.

## ASSUMPTIONS to confirm with Lodge staff

1. **Capacities are confirmed for all 10 plans** (Samuel/manager input 2026-10-31, M-STD-3 = 6 included). Private rooms can still lower an apartment's real headcount.
2. **Housing category (women or men) is on the floor plan,** and each category has its own building. A tenant's eligibility comes from their contracted plan.
3. A tenant has exactly one contract per term.
4. "Current apartment" for a renewal means the apartment they live in during the previous term.
5. `has_animal` means an approved assistance animal/ESA will live in the apartment (entered by staff from the Lodge's Reasonable Accommodation Process). `animal_allergy_restriction` currently stands for a self-reported allergy; the design doc proposes that only staff-confirmed documentation makes it a hard rule. Whether it also applies to neighbouring apartments is unknown.
6. Placement restrictions (`required_floor`, `excluded_floor`, required or excluded apartment, `must_not_share_with`) are hard rules and are entered only after staff confirm them.
7. **"Nearby" for renewal moves means the same floor and the same ward** (Samuel/manager input). Whether an adjacent floor counts at all is unknown, and the apartment-to-ward mapping is not available yet.
8. **How many apartments of each plan are on each floor is unknown.** The seeds spread each plan across floors 1 to 4 in turn, and the apartment numbers are random.
9. Roommate requests are stored one row per named person. Group requests are treated as the sum of those rows.
10. The term dates are fictional placeholders. Terms are Fall, Winter and Spring only (Samuel/manager input).
11. Samuel gave priority weights; turning them into points (tiers, caps) is a draft in `docs/assignment-algorithm.md`, not decided. `assignment_drafts.priority_config` stays free-form JSON until then.
12. Lower-priority fields (age, social preference, major, academic stage) are optional and only soft preferences. Similar values don't mean better roommates.
13. **The private-room option is part of the contract** (presumed; not yet in the schema).
14. **Each apartment's ward is fixed across semesters** (Samuel believes so; to confirm). Ward mapping not provided yet.
15. **Tenants with a staff-confirmed accessibility need must get a handicap-adapted apartment.** Whether those apartments must stay reserved otherwise is unknown.
