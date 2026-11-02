# Roomatch — team overview

Short guide for teammates. Fictional Lodge data only — not ready for real student records.

## What exists today

| Piece | What it is |
|---|---|
| Manual assignment workspace | Pick a term/draft; move, swap, lock tenants into apartments |
| Backend validation | Shared C# rules: floor plan, capacity, locks, staff restrictions |
| History / undo | Successful edits recorded; undo last or by row (history panel collapsed by default) |
| Roommate request prep | Review pending names; resolve duplicates or mark missing/ambiguous/excluded |
| Auto-match v1 | Generate a new draft: mutual resolved pairs → soft one-way → greedy fill |

Details: `docs/manual-assignment.md`, `docs/roommate-request-prep.md`, `docs/auto-match-v1.md`.

## How to run locally (Mac)

1. **MySQL** — user-space `~/mysql-lodge` (socket, port 3307). Ensure mysqld is up.
2. **Schema + seed** (once): load `db/schema.sql` then `db/seed_small.sql` into `lodge_small`.
3. **Migration:**  
   `mysql … lodge_small < db/migrations/001_assignment_change_history.sql`
4. **API (.NET 8):**
   ```bash
   export DOTNET_ROOT="$(brew --prefix dotnet@8)/libexec"
   export PATH="$DOTNET_ROOT:$PATH"
   export ConnectionStrings__Lodge="Server=$HOME/mysql-lodge/run/mysql.sock;Protocol=Unix;Database=lodge_small;User ID=root;Password="
   cd backend/Lodge.Api && dotnet run --launch-profile http
   ```
   Health: `http://localhost:5080/api/health`
5. **UI:** `cd frontend && npm install && npm run dev` → `http://localhost:5173` (proxies `/api` → 5080).

Use a fictional **Operator identity** in the UI (no real login yet).

## Suggested walkthrough

1. Select term **W2027**.
2. Optionally open **Show roommate prep** — fix a few names.
3. **Generate draft (v1 match)** — new draft with placements; summary banner shows counts.
4. Edit apartments manually; expand **Show history** only when you need undo.

## Not built yet

- Full preference/weight optimizer, CP-SAT, what-if
- Real auth / roles, cloud deploy
- Auto-resolving misspellings without staff confirm
- Ward / accessibility / private-room capacity (proposals only)
- Real student data import

`docs/assignment-algorithm.md` is a design draft — treat it as proposals, not shipped policy.
