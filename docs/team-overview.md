# Roomatch — team overview

Short guide for teammates. Fictional Lodge data only — not ready for real student records.

## What exists today

| Piece | What it is |
|---|---|
| **Make first plan** | Primary action: pick a term → generate an automatic working plan |
| Apartment boxes | Colored cells of students; Lodge-like numbers (3xxx women / 2xxx men) |
| Women / Men tabs | Board + waiting list scoped per building; click Move (no drag-and-drop) |
| Student details | Click a student → modal with contract fields from the API |
| Student chip colors | Renewing (warm) vs new (blue); **Color guide** collapsed by default |
| Light edits | Move, unassign, swap, lock (same validated APIs as before) |
| Advanced tools | Roommate-name prep, history/undo, copy plan, operator name (collapsed) |
| Backend validation | Floor plan, capacity, locks, staff restrictions |

Details: `docs/manual-assignment.md`, `docs/roommate-request-prep.md`, `docs/auto-match-v1.md`.

## Manager flow (demo)

1. Open the UI (`npm run dev` → usually `http://127.0.0.1:5173`; use `:5174` if that port is taken).
2. Confirm **Term** (e.g. W2027).
3. Click **Make first plan** — apartments fill as boxes; summary banner shows counts.
4. Click a student → **Adjust** (move / unassign / swap).
5. Open **Advanced tools** only for roommate names, undo history, or copy.

## How to run locally (Mac)

1. **MySQL** — `~/mysql-lodge` (socket, port 3307).
2. Schema + `db/seed_small.sql` into `lodge_small`; apply `db/migrations/001_assignment_change_history.sql`.
3. **API (.NET 8):**
   ```bash
   export DOTNET_ROOT="$(brew --prefix dotnet@8)/libexec"
   export PATH="$DOTNET_ROOT:$PATH"
   export ConnectionStrings__Lodge="Server=$HOME/mysql-lodge/run/mysql.sock;Protocol=Unix;Database=lodge_small;User ID=root;Password="
   cd backend/Lodge.Api && dotnet run --launch-profile http
   ```
4. **UI:** `cd frontend && npm install && npm run dev` → `:5173` (proxies `/api` → `:5080`).

## Not built yet

Full preference optimizer, real auth, auto name-guess without staff, wards/accessibility schema, real student import, cloud.
