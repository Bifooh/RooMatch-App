# Lodge tenant-assignment MVP

**All tenant data here is FICTIONAL.** No real resident data was used, and none should go into these files. The floor plan names, the two buildings and the four floors are real Lodge facts from Samuel. Resident capacities are still placeholders.
Real-data deployment needs its own security and operations review.

This repo holds the MySQL schema and a reproducible fictional dataset (deliverable 1), plus the app skeleton:
a Vite + React + TypeScript frontend and an ASP.NET Core (.NET 8) minimal API. The rules engine and solver come later.
Everything runs locally: no cloud services, telemetry or external APIs.

## Contents

| Path | What it is |
|---|---|
| `db/schema.sql` | MySQL 8 schema: 11 tables plus the `v_apartment_capacity` view. Re-running it drops and recreates the tables. |
| `db/seed_small.sql` | 32 tenants and 18 apartments, seed 2027. It needs at least 32 tenants to fit every edge case. |
| `db/seed_large.sql` | 1,100 tenants and 272 apartments, seed 2027. |
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
curl localhost:5080/api/summary   # {"tenants":32,"apartments":18,"floorPlans":10}
```

`appsettings.Local.json` is git-ignored and overrides `appsettings*.json`. You can also use
`ConnectionStrings__Lodge=...` as an environment variable. Without either, the Development default
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
- **Git author:** no git identity was configured, so the initial commit uses the local (repo-only) placeholder `Samuel Pereira <samuel@localhost>`. Fix with `git config user.email ...` and amend if wanted.

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

## Floor plans (real names; capacities are PLACEHOLDERS)

| Code | Plan (exact name) | Variant | Placeholder capacity |
|---|---|---|---|
| W-STD-2 | Women's - Standard. 2 Bedroom, 2 Bath | standard | 4 |
| W-OVR-2 | Women's - Oversized. 2 Bedroom, 2 Bath | oversized | 4 |
| W-STD-3 | Women's - Standard. 3 Bedroom, 3 Bath | standard | 6 |
| W-FLAT-4 | Women's - Flat. 4 Bedroom, 4 Bath | flat | 4 |
| W-STH-4 | Women's - Standard Townhome. 4 Bedroom, 4 Bath | standard_townhome | 4 |
| W-OTH-4 | Women's - Oversized Townhome. 4 Bedroom, 4 Bath | oversized_townhome | 4 |
| M-STD-3 | Men's - Standard. 3 Bedroom, 3 Bath | standard | 6 |
| M-FLAT-4 | Men's - Flat. 4 Bedroom, 4 Bath | flat | 4 |
| M-STH-4 | Men's - Standard Townhome. 4 Bedroom, 4 Bath | standard_townhome | 4 |
| M-OTH-4 | Men's - Oversized Townhome. 4 Bedroom, 4 Bath | oversized_townhome | 4 |

The men's building has no 2-bedroom plans. Capacities are hard-coded per plan in `tools/DataGen/Program.cs`, never computed from bedrooms, and flagged `capacity_is_assumption = TRUE`. **Replace them with the real numbers.**

## Deliberate edge cases (the same set in small and large)

| ID | Case |
|---|---|
| E01 | A group of 5 who all request each other, in W-STD-2 (holds 4). |
| E02 | A mutual request where one tenant has an animal and the other has an animal-allergy restriction (M-STD-3). |
| E03 | A misspelled roommate name (W-STD-3; the reverse request is spelled correctly). |
| E04 | A request naming a person who doesn't exist ("Rowan Glimmerpatch"). |
| E05 | A non-mutual request (M-STH-4). |
| E06 | An ambiguous request ("Jordan") with two Jordans in M-FLAT-4. |
| E07 | A cross-category request: a W-STD-3 tenant names an M-STD-3 tenant (different building). |
| E08 | A mutual request between M-STD-3 and M-OTH-4 (different contracted plans). |
| E09 | Two renewing tenants who want to stay together in their W-STD-3 apartment. |
| E10 | A renewing M-STD-3 tenant who wants to move (prefer the same building and floor, then an adjacent floor). |
| E11 | A renewing tenant who wants to stay, but lives in M-STD-3 and contracted M-FLAT-4. |
| E12 | A staff-confirmed `required_floor = 1`, with a manager-locked pre-placement on floor 1. |
| E13 | A manager-locked renewal stay (W-OVR-2). |
| E14 | A staff-confirmed `must_not_share_with` pair in M-FLAT-4, where one of them requested the other. |
| E15 | One oversubscribed plan, **M-OTH-4**: 6 contracts for 4 places in small, 87 for 80 in large. Total capacity is still enough overall (79 for 32 tenants in small, 1,229 for 1,100 in large). |
| E16 | One W-STD-3 apartment with `capacity_override = 5` (placeholder plan capacity 6). |

The 2 locked assignments live in draft 1, "Manager pre-placements (fictional)".

The **large** set also adds random noise, all listed in its answer key:
- About 3% of tenants have an animal and about 4% have an allergy restriction.
- 190 roommate requests are non-mutual because the reverse request was dropped.
- 43 roommate names are randomly misspelled.
- 4 requests name people who don't exist.
- About a third of tenants are renewals.

The **small** set has no random animals or allergies, so every conflict in it is deliberate.

## ASSUMPTIONS to confirm with Lodge staff

1. **Resident capacity per plan is unknown.** The placeholders are listed in the table above.
2. **Housing category (women or men) is on the floor plan,** and each category has its own building. A tenant's eligibility comes from their contracted plan.
3. A tenant has exactly one contract per term.
4. "Current apartment" for a renewal means the apartment they live in during the previous term.
5. `has_animal` means an animal will live in the apartment. An allergy restriction is a hard rule against sharing with that tenant. Whether it also applies to neighbouring apartments is unknown.
6. Placement restrictions (`required_floor`, `excluded_floor`, required or excluded apartment, `must_not_share_with`) are hard rules and are entered only after staff confirm them.
7. **"Nearby" means the same building and floor, then an adjacent floor.** Distance within a floor is unknown.
8. **How many apartments of each plan are on each floor is unknown.** The seeds spread each plan across floors 1 to 4 in turn, and the apartment numbers are random.
9. Roommate requests are stored one row per named person. Group requests are treated as the sum of those rows.
10. The term names and dates are fictional placeholders.
11. Priorities and weights are not decided. `assignment_drafts.priority_config` is free-form JSON until the team picks an order.
12. Lower-priority fields (age, social preference, major, academic stage) are optional and only soft preferences. Similar values don't mean better roommates.
