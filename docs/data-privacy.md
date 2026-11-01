# Roomatch: data privacy and security (DRAFT)

**Status:** draft for team review, 2026-10-31. Nothing here is legal advice, and nothing here says what the law
requires. It sets out how we *intend* to handle student data and lists what we must ask the Lodge.

**The MVP uses fictional data only.** No real student data may be loaded, copied, or tested with until the Lodge has
approved it in writing and the security review in section 10 is done.

**Decision labels used below**

| Label | Meaning |
|---|---|
| **Recommended default (likely uncontroversial)** | We expect the team and reviewers to agree. Adopt unless someone objects. |
| **Team decision needed** | A real tradeoff. The team (and possibly the Lodge) must choose. |

## 1. Why this matters

The Lodge will only work with us if we handle student data carefully. The data is about students, often
under 21 and sometimes under 18. It includes health-related answers (allergy or disability), names of other
people (roommate requests), and religious organization (wards). The safest data is data we never collect, so the
main tool is **minimization**: the assignment engine needs very little.

## 2. Principles

| # | Principle | Label |
|---|---|---|
| P1 | **Collect the minimum.** Store only the fields the assignment needs (section 3). Everything else is dropped at import and never enters the database. | Recommended default |
| P2 | **Store derived values, not raw ones.** Store the age, never the birthday. Store a yes/no "cannot live with an animal", never a diagnosis, allergy type or disability detail. | Recommended default |
| P3 | **Pseudonymous tenant codes.** Every table refers to a tenant by a code. Real names live only in the separate identity table (`tenants`), used for matching roommate names and for staff display. | Recommended default |
| P4 | **Never store:** home address, phone numbers, any email address, birthday, emergency contact, parking, payment or deposit details, free-form comments, or marketing source ("how did you hear about us"). | Recommended default |
| P5 | **Local only.** One computer, no cloud services, no telemetry, no external AI APIs, no CDN or analytics in the web app. The API listens on localhost only. | Recommended default |
| P6 | **No real data in AI tools.** Never paste real student data (or screenshots of it) into ChatGPT, Claude, Grok, Copilot, Cursor or any other AI tool, including to "debug quickly". Use the fictional seeds instead. | Recommended default |
| P7 | **Real data never touches the repo.** No real exports, dumps or screenshots in git, issues or chat. | Recommended default |
| P8 | **Purge after use.** Personal data is deleted once a term's assignment is final and the retention period ends (section 7). | Recommended default (the length is a team decision) |

## 3. Field-by-field: what we keep from each form

**Form 1** is the website application filled before the deposit. **Form 2** is the roommate-matching form filled
after the security deposit is paid and before the semester. Only the field lists are recorded here; no real values
were kept.

"Needed?" means needed for assignment: **yes**, **no**, or **derived** (used to compute something smaller, then dropped).

| Field | Form | Needed? | What we store instead | Sensitivity | Reason |
|---|---|---|---|---|---|
| Legal first and last name | 1 and 2 | yes | First and last name in the identity table only | Medium | Match roommate requests; staff display. |
| Home address (street, address 2, city, state, zip) | 1 | no | nothing | High | Not used in assignment. |
| Birthday | 1 | derived | nothing (age comes from the age field) | High | Age is enough; a birthday plus name identifies a person. |
| Phone | 1 | no | nothing | High | Not used. |
| BYU-I email | 1 and 2 | derived | nothing, or a link key (see D4) | High | Needed only to link Form 1, Form 2 and the contract at import. |
| Personal email | 1 | no | nothing | High | Not used. |
| Gender (Male/Female) | 1 | derived | nothing | Medium | The contracted floor plan already fixes the building. Used at import only to flag a mismatch for staff. |
| Age (0-99) | 1 and 2 | yes | Age as an integer (or an age band, see D3) | Medium | Optional low-weight extra; staff may need to know about under-18s. |
| "Do you have any allergies or other disability that would prevent you from living with an assistance animal?" (Yes/No) | 1 | yes | One boolean: `animal_allergy_restriction` ("cannot live with an animal") | **High** (health/disability related) | Hard rule H5. The answer is already yes/no; no details are ever stored. |
| Semester/Year (Fall 2026, Winter 2027, Spring 2027) | 1 and 2 | yes | Term | Low | Which term the contract is for. |
| Floor plan checkboxes (the 10 plans) | 1 | yes | Contracted floor plan (taken from the signed contract, not the application) | Low | Hard rule H1. The contract is the authority if the two differ. |
| "Not seeing the floorplan you want? Join the Waitlist" | 1 | no | nothing | Low | The waitlist is out of scope. |
| Roommate request (optional free text) | 1 | yes | Original wording, per term | Medium (names other people) | Request matching. |
| Second Semester (optional, $50 off both leases) | 1 | yes | A link between the tenant's two term contracts | Low | Continuity preference (algorithm doc S1b). |
| Parking pass section | 1 | no | nothing | Medium | Not used. |
| Emergency contact (first/last, phone, email) | 1 | no | nothing | High | Not used; belongs to a third person. |
| Comments | 1 | no | nothing | **Unknown, possibly high** | Free text can contain anything. If staff see something relevant, they enter it as a structured, staff-confirmed restriction. |
| How did you hear about The Lodge | 1 | no | nothing | Low | Marketing, not assignment. |
| BYUI email | 2 | derived | as above | High | Import link only. |
| Semester | 2 | yes | Term | Low | |
| First and last name | 2 | yes | Identity table | Medium | |
| Current apartment number (or N/A) | 2 | yes | Current apartment for renewals (checked against Lodge records) | Low | Renewal stay and nearby moves. |
| People to live with (first + last names) | 2 | yes | Original wording, per term | Medium | Request matching. |
| Other requests (e.g. "124th ward") | 2 | yes, after staff review | The structured result (e.g. a ward request); raw text only until staff review it (see D5) | **Unknown, possibly high** | Free text can contain health or personal details. |
| Age | 2 | yes | Age (as above) | Medium | |
| Cleanliness 1-5 | 2 | yes | Integer 1-5 | Low | Scored preference. |
| Shy/Outgoing | 2 | yes | One of two values | Low | Low-weight extra. |
| Typical bedtime (8-9 pm, 10-11 pm, 12-1 am, later than 1 am) | 2 | yes | One of four buckets | Low | Scored preference. |
| School year (Freshman/Sophomore/Junior/Senior) | 2 | yes | One of four values | Low | Low-weight extra. |
| Major (free text) | 2 | yes (optional) | Free text, trimmed | Low | Low-weight extra. |

Other data the engine uses that is not on either form: **ward of each apartment** (from staff), **accessible
apartments** (from staff), **staff placement restrictions** (rule only, never the reason), and **who has an animal**.
Neither form asks whether the tenant *has* an animal; that presumably comes from the Lodge's assistance-animal
request process (open question Q6). We would store only a yes/no `has_animal`.

## 4. Decisions

| ID | Decision | Label |
|---|---|---|
| D1 | Adopt the field table in section 3 as the import allow-list. | Recommended default |
| D2 | Names stay in `tenants`; every other table uses tenant IDs. Staff screens show names; exports and logs use tenant codes. | Recommended default |
| D3 | Store age as an integer from the form (not birthday). Optionally store only an age band (e.g. under 18, 18-20, 21-23, 24+). | Team decision needed (integer is simpler; bands are more private) |
| D4 | How to link a student across forms and terms without keeping emails: (a) use the Lodge's own resident/contract ID if the export has one; (b) a keyed hash of the BYU-I email with a secret kept only on the machine; (c) staff link by hand. | Team decision needed ((a) preferred if available) |
| D5 | "Other requests" free text: keep it only until staff convert it into structured data (ward request, staff restriction), then delete it. | Team decision needed (vs keeping it for the term, or never importing it) |
| D6 | Gender is not stored. A mismatch between gender and the plan's building is shown to staff at import and not saved. | Recommended default |
| D7 | Encryption at rest via full-disk encryption on the one computer (BitLocker, FileVault or LUKS). | Recommended default |
| D8 | Also turn on MySQL InnoDB tablespace encryption. | Team decision needed (more protection, more key management) |
| D9 | Retention: delete all personal data for a term N days after the assignment is final (e.g. after the semester's add/drop week). Keep only counts. | Team decision needed (the value of N; also ask the Lodge) |
| D10 | Staff roles and login before any real data (section 5). | Recommended default (the MVP has no login today) |

## 5. Who can see what

The MVP has **no login at all**, which is fine for fictional data and not acceptable for real data. Proposed roles:

| Role | Can see | Can do |
|---|---|---|
| Housing manager | Everything in the app, including that a staff restriction exists (never its reason) | Confirm restrictions, overrides and accessibility needs; lock; finalize a draft; run imports and purges |
| Assignment staff | Names, plans, preferences, requests, drafts | Edit drafts, resolve requests, work the review queue |
| Read-only staff | Final assignments (names, apartment numbers) | Nothing |
| Developers (students) | **Fictional data only** | Build and test. No access to real data unless the Lodge explicitly allows it. |

The confidential reason behind a restriction (for example a conflict between two students) is never stored in
Roomatch at all, so no role can see it here.

## 6. Storage, encryption and backups

- **One computer**, owned or approved by the Lodge, with full-disk encryption (D7), a screen lock and an OS account
  per person. MySQL listens on a local socket or localhost only.
- **Backups** are made only if needed, are encrypted, stay on Lodge-approved media, and follow the same retention
  (a backup is still personal data).
- No sync folders (OneDrive, Dropbox, Google Drive, iCloud) for the database, exports or backups.

## 7. Retention and purge

- Data is per term. When a term's assignment is final and the retention period (D9) has passed, delete that term's
  contracts, preferences, requests, restrictions and assignments, then any tenant identity no longer used.
- Renewals do not need old history: each new term's import says where a renewing tenant lives now.
- The purge is a single, logged command, and the log records only counts.

## 8. Import path

1. Staff export a CSV from the Lodge's own systems (or the form tool), ideally already limited to the allowed columns.
2. The importer reads **only allow-listed columns** (D1). Any other column is ignored and never written anywhere,
   including logs and error messages.
3. Values are checked (valid plan, term, bucket, 1-5 range). Problems go to an import report that uses row numbers
   and tenant codes, not names or emails.
4. **The original file is deleted** after a successful import. Deletion on SSDs is not reliably permanent, which is
   another reason full-disk encryption (D7) matters.

## 9. Logging

- No personal data in logs: no names, emails, request text or preference values. Use tenant codes and IDs.
- Do not log SQL parameters or request bodies. Keep ASP.NET Core logging at Information or above and check that
  exception messages don't echo row data.
- The browser app has no analytics, tracking or third-party scripts.

## 10. Before any real data

1. Written approval from the Lodge, covering which fields, which computer, which people, and for how long.
2. A security review: login and roles (D10), encryption (D7, D8), backups, import, purge, logging, dependency
   updates.
3. A dry run of import and purge on fictional data.
4. A named contact at the Lodge for questions and incidents.

## 11. Questions to ask the Lodge

These are questions, not conclusions. We are not claiming any law does or doesn't apply.

1. Does the Lodge consider any of this data covered by **FERPA**, or by an agreement with **BYU-Idaho** (for example
   as BYU-I approved housing)? Are there BYU-I policies on how approved housing handles student data?
2. **Who owns the data**, and do we need a written data-sharing or confidentiality agreement? May student volunteers
   or contractors work with it?
3. Do the forms' privacy notices cover this use? Does the Lodge want students told about automated matching?
4. How long does the Lodge want us to keep the data, and does it have its own retention rule?
5. Can the export include the Lodge's own resident or contract ID (D4), so we never need emails?
6. Where does "has an animal" come from (the assistance-animal process?), and may we receive only a yes/no?
7. Which computer will run it, who administers it, and is it already encrypted?
8. Who should we contact if something goes wrong (a lost laptop, a mistaken export)?
9. Are there state-law or other requirements the Lodge already follows for resident data?

## 12. Review questions for other AI tools

1. Is any field in section 3 kept that the assignment doesn't really need? Is any needed field missing?
2. Are age, school year and major worth keeping at all, given their low weights?
3. Which is the best cross-term link for D4, and what are its risks?
4. Is a keyed hash of an email really pseudonymous here? What could re-identify someone?
5. What are the risks of free-text fields (roommate requests, other requests), and how should they be handled?
6. What does a minimal but adequate login and role model look like for a single-computer, local app?
7. Is full-disk encryption enough, or is database-level encryption worth the key management?
8. What should the purge delete, and how can we prove it ran?
9. What questions to the Lodge are missing from section 11?
10. What could go wrong with logs, crash dumps, browser storage or backups that this draft overlooks?
