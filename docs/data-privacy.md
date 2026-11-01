# Roomatch: data privacy and security (DRAFT)

**Status:** draft for team review. Nothing here is legal advice, and nothing here says what the law
requires. It sets out how we *intend* to handle student data and lists what we must ask the Lodge.

**The MVP uses fictional data only.** No real student data may be loaded, copied, or tested with until the Lodge has
approved it in writing and the security review in section 10 is done.

**Labels** (same as [assignment-algorithm.md](assignment-algorithm.md#how-to-read-the-labels))

| Label | Meaning |
|---|---|
| **CONFIRMED PRODUCT DECISION** | Decided by Samuel's team. |
| **PROPOSED TECHNICAL DEFAULT** | Our suggestion; needs the team's explicit approval. |
| **QUESTION FOR LODGE CONFIRMATION** | Only authorized Lodge staff can answer it. |

## 1. Why this matters

The Lodge will only work with us if we handle student data carefully. The data is about students, often
under 21 and sometimes under 18. It includes health-related answers (allergy or disability), names of other
people (roommate requests), and religious organization (wards). The safest data is data we never collect, so the
main tool is **minimization**: the assignment engine needs very little.

## 2. Principles

| # | Principle | Label |
|---|---|---|
| P0 | **Fictional data only** in the MVP, on one computer. | CONFIRMED PRODUCT DECISION |
| P1 | **Collect the minimum.** Store only the fields the assignment needs (section 3). Other fields may pass through memory while a file is parsed, but are never stored, logged or displayed (section 8). | PROPOSED TECHNICAL DEFAULT |
| P2 | **Store derived values, not raw ones.** Store the age, never the birthday. Store only the Yes/No allergy answer and whether staff confirmed documentation, never a diagnosis, allergy type, condition explanation or disability detail. | PROPOSED TECHNICAL DEFAULT |
| P3 | **Pseudonymization, not anonymity.** Tables refer to tenants by ID and code, and names live in the separate identity table (`tenants`), used only for matching roommate names and for staff display. This **reduces** exposure but does **not** make the data anonymous: anyone with database access can join back to names, and even without names, apartment, plan, age and preferences can identify a person. Treat every table as personal data. | PROPOSED TECHNICAL DEFAULT |
| P4 | **Never store:** home address, phone numbers, any email address, birthday, emergency contact, parking, payment or deposit details, free-form comments, marketing source ("how did you hear about us"), the Form 2 condition explanation, allergy documentation, or the Events & Amenities page. | PROPOSED TECHNICAL DEFAULT |
| P5 | **Local only.** One computer, no cloud services, no telemetry, no cloud AI or external AI APIs, no CDN or analytics in the web app. The API listens on localhost only. | CONFIRMED PRODUCT DECISION (one computer, no cloud AI); details PROPOSED TECHNICAL DEFAULT |
| P6 | **Real resident data stays outside developer tools and AI chats.** Never paste real data (or screenshots of it) into ChatGPT, Claude, Grok, Copilot, Cursor or any other AI tool, or into the repo, issues, PRs or chat. Developers work with the fictional seeds only. | CONFIRMED PRODUCT DECISION |
| P7 | **Staff notes stay local** and are visible only to authorized property staff, not developers. | CONFIRMED PRODUCT DECISION |
| P8 | **Purge after use** (section 7). | PROPOSED TECHNICAL DEFAULT; the retention period is a QUESTION FOR LODGE CONFIRMATION |

## 3. Field-by-field: what we keep from each form

**Form 1** is the website application filled before the deposit. **Form 2** is the roommate-matching form filled
after the security deposit is paid and before the semester; besides the matching questions it has an Emotional
Support Animal information page, a Winter 2027 Events & Amenities page and a last page of minor items. Only the field lists are recorded here; no real values
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
| "Do you have any allergies or other disability that would prevent you from living with an assistance animal?" (Yes/No) | 1 | yes | One boolean, `allergy_self_reported` (Yes on either form) | **High** (health/disability related) | Self-reported allergy concern (algorithm doc 4.3). No details. |
| Semester/Year (Fall 2026, Winter 2027, Spring 2027) | 1 and 2 | yes | Term | Low | Which term the contract is for. |
| Floor plan checkboxes (the 10 plans) | 1 | yes | Contracted floor plan (taken from the signed contract, not the application) | Low | Hard rule H1. The contract is the authority if the two differ. |
| "Not seeing the floorplan you want? Join the Waitlist" | 1 | no | nothing | Low | The waitlist is out of scope. |
| Roommate request (optional free text) | 1 | yes | Original wording, per term | Medium (names other people) | Request matching. |
| Second Semester (optional, $50 off both leases) | 1 | yes | A link between the tenant's two term contracts | Low | Continuity, source (b) of S1 in the algorithm doc. |
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
| Emotional Support Animal information (policy text) | 2 | no | nothing | n/a | Informational page; no answer to store. |
| "Do you have any allergies or medical reasons that would prevent you from residing with a roommate that has an emotional support animal/service animal?" (Yes/No, required) | 2 | yes | Same boolean, `allergy_self_reported` | **High** (health) | Self-reported allergy concern. |
| "If yes to the previous question, please explain your condition." (optional free text) | 2 | **no** | **nothing, ever** | **Very high** (health information) | **Never imported or stored.** The importer must not read this column at all. |
| Allergy documentation (doctor's note or allergy results) | emailed to the Lodge manager | no | Only `allergy_confirmed_by` / `allergy_confirmed_at` (who confirmed, when) | **Very high** | Stays with the Lodge manager, outside Roomatch. |
| Winter 2027 Events & Amenities page (favorite gift-card stores, event type, raffle ideas, event requests, amenity suggestions) | 2 | no | nothing | Low | Doesn't affect matching; never stored. |
| Last page (minor items) | 2 | no | nothing | Unknown | Doesn't affect matching; never stored. |

Other data the engine uses that is not on either form: **ward of each apartment** (from staff), **accessible
apartments** (from staff), **staff placement restrictions** (rule only, never the reason), and **who has an approved
animal**. Neither form asks whether the tenant *has* an animal. That comes from the Lodge's Reasonable Accommodation
Process: staff enter a yes/no `has_animal` with who approved it and when, never the animal type, the disability or the
paperwork.

## 4. Decisions

| ID | Decision | Label |
|---|---|---|
| D1 | Adopt the field table in section 3 as the import allow-list. | PROPOSED TECHNICAL DEFAULT |
| D2 | Names stay in `tenants`; other tables use tenant IDs. Staff screens show names; exports and logs use tenant codes. | PROPOSED TECHNICAL DEFAULT |
| D3 | Store age as an integer from the form, never the birthday. (Alternative: an age band only.) | PROPOSED TECHNICAL DEFAULT |
| D4 | Identity across forms and terms: prefer the Lodge's own resident ID; otherwise a keyed hash of the BYU-I email (secret kept only on the machine) or staff linking by hand. Names alone never establish identity (duplicate names exist). | PROPOSED TECHNICAL DEFAULT; whether the ID is available is a QUESTION FOR LODGE CONFIRMATION |
| D5 | "Other requests" free text: kept only until staff convert it into structured data (ward request, staff restriction), then deleted. | PROPOSED TECHNICAL DEFAULT |
| D6 | Gender is not stored; a plan/gender mismatch is shown at import and not saved. | PROPOSED TECHNICAL DEFAULT |
| D7 | Full-disk encryption on the one computer (BitLocker, FileVault or LUKS). | PROPOSED TECHNICAL DEFAULT |
| D8 | MySQL InnoDB tablespace encryption on top of D7. Not enabled by default (more key management). | PROPOSED TECHNICAL DEFAULT |
| D9 | One retention period per term for all personal data in Roomatch (section 7). | PROPOSED TECHNICAL DEFAULT; the period is a QUESTION FOR LODGE CONFIRMATION |
| D10 | Staff login and roles before any real data (section 5). The MVP has none today. | PROPOSED TECHNICAL DEFAULT |
| D11 | The Form 2 condition explanation, allergy documentation and the Events & Amenities page are never stored; only the Yes/No answer and a staff "documentation confirmed" flag (who/when). | PROPOSED TECHNICAL DEFAULT |
| D12 | Roomatch **does not automatically delete** the manager's original import file. | CONFIRMED PRODUCT DECISION |

## 5. Who can see what

The MVP has **no login at all**, which is fine for fictional data and not acceptable for real data. Proposed roles
(PROPOSED TECHNICAL DEFAULT; who holds them is a QUESTION FOR LODGE CONFIRMATION):

| Role | Can see | Can do |
|---|---|---|
| Housing manager | Everything in the app, including that a staff restriction exists (never its reason) | Confirm restrictions, overrides, accessibility needs, allergy documentation and approved animals; lock; finalize a draft; run imports and purges |
| Assignment staff | Names, plans, preferences, requests, drafts | Edit drafts, resolve requests, work the review queue |
| Read-only staff | Final assignments (names, apartment numbers) | Nothing |
| Developers (students) | **Fictional data only**; never real data, staff notes or real change history | Build and test. |

The confidential reason behind a restriction (for example a conflict between two students) is never stored in
Roomatch at all, so no role can see it here.

## 6. Storage, encryption and backups

- **One computer**, owned or approved by the Lodge, with full-disk encryption (D7), a screen lock and an OS account
  per person. MySQL listens on a local socket or localhost only.
- **Backups** are made only if needed, are encrypted, stay on Lodge-approved media, and follow the same retention
  (a backup is still personal data).
- No sync folders (OneDrive, Dropbox, Google Drive, iCloud) for the database, exports or backups.

## 7. Retention

PROPOSED TECHNICAL DEFAULT: everything below belongs to one term and is purged together when that term's retention
period ends. The period itself is a QUESTION FOR LODGE CONFIRMATION.

| Data | Where it lives | Retention (proposed) |
|---|---|---|
| Original request wording (`roommate_requests.raw_text`) | Database | Until the term purge. Needed to show staff exactly what was asked and why a request was excluded. |
| Structured requests (resolved tenant, status, exclusion reason; ward requests) | Database | Until the term purge. |
| "Other requests" raw text | Database | Only until staff convert it (D5), then deleted. |
| Review-queue copies (`review_queue.original_wording`, `reason`) | Database | Until the term purge, same as the request they copy. Purging requests must purge these copies too. |
| Staff notes on edits | Database | Until the term purge; authorized staff only (P7). |
| Change history and undo records | Database | Until the term purge. |
| Baseline (generated) draft and final assignment | Database | Until the term purge. |
| Success-measure counts (acceptance %, numbers of changes by reason, times) | Database or report | May be kept longer **only as counts with no tenant IDs**. |
| Backups | Encrypted, Lodge-approved media | Same period; a backup is still personal data. |
| The manager's original import file | Wherever the manager keeps it, outside Roomatch | Not deleted by Roomatch (D12). The manager handles it under Lodge rules (QUESTION FOR LODGE CONFIRMATION). |

- Renewals do not need old history: each new term's import says where a renewing tenant lives now.
- The purge is a single, logged command; the log records only counts.

## 8. Import path and what is parsed, stored, logged or retained

1. The manager exports a CSV from the Lodge's systems (or the form tool), ideally already limited to the allowed
   columns and without the condition-explanation column.
2. The importer reads the file **from where the manager put it** and never copies it into the repo, the app folders or
   anywhere else.
3. CSV parsing reads whole rows into memory, so non-allowed columns are briefly in memory. They are **discarded
   right after parsing** and never stored, logged, displayed or sent anywhere.
4. Values are checked (valid plan, term, bucket, 1-5 range). Problems go to an import report using row numbers and
   tenant codes, not names or emails.
5. **Roomatch does not delete the original file** (D12). Deleting or keeping it is the manager's decision under
   Lodge rules. Deletion on SSDs is not reliably permanent anyway, which is one reason for full-disk encryption (D7).

| Data | Parsed temporarily (memory) | Stored (database) | Logged | Retained |
|---|---|---|---|---|
| Allow-listed fields (section 3) | Yes | Yes | No (IDs and counts only) | Until the term purge (section 7) |
| Non-allowed columns (address, phone, emails, birthday, emergency contact, parking, comments, marketing source, events page) | Yes, during parsing only | **No** | **No** | No |
| Condition explanation (Form 2) | Only if the export includes it; discarded immediately | **No** | **No** | No |
| BYU-I email (as link key) | Yes | No (or a keyed hash, D4) | No | Hash, if used: until the term purge |
| Gender | Yes, for the mismatch check | No | No | No |
| Import report | Built in memory | Optional, row numbers and tenant codes only | Counts only | Until the term purge |

## 9. Logging

- No personal data in logs: no names, emails, request text or preference values. Use tenant codes and IDs.
- Staff notes and change-history details are never written to logs.
- Do not log SQL parameters or request bodies. Keep ASP.NET Core logging at Information or above and check that
  exception messages don't echo row data.
- The browser app has no analytics, tracking or third-party scripts.

## 10. Before any real data

1. Written approval from the Lodge, covering which fields, which computer, which people, and for how long.
2. A security review: login and roles (D10), encryption (D7, D8), backups, import, purge, logging, dependency
   updates.
3. A dry run of import and purge on fictional data.
4. A named contact at the Lodge for questions and incidents.

## 11. Questions for Lodge confirmation

Each item is a QUESTION FOR LODGE CONFIRMATION. These are questions, not conclusions. We are not claiming any law does
or doesn't apply.

1. Does the Lodge consider any of this data covered by **FERPA**, or by an agreement with **BYU-Idaho** (for example
   as BYU-I approved housing)? Are there BYU-I policies on how approved housing handles student data?
2. **Who owns the data**, and do we need a written data-sharing or confidentiality agreement? May student volunteers
   or contractors work with it?
3. Do the forms' privacy notices cover this use? Does the Lodge want students told about automated matching?
4. How long does the Lodge want us to keep the data, and does it have its own retention rule?
5. Can the export include the Lodge's own resident or contract ID (D4), so we never need emails?
6. Can staff give us only a yes/no for approved animals (Reasonable Accommodation Process) and for confirmed
   allergy documentation, and can the export leave out the condition-explanation column entirely?
7. Which computer will run it, who administers it, and is it already encrypted?
8. Who should we contact if something goes wrong (a lost laptop, a mistaken export)?
9. Are there state-law or other requirements the Lodge already follows for resident data?
10. How long should staff notes, change history, request wording and review-queue copies be kept, and what should
    happen to the manager's original import files?
11. Which Lodge staff roles may see notes and history, and who approves new users?

## 12. Review questions for other AI tools

1. Is any field in section 3 kept that the assignment doesn't really need? Is any needed field missing?
2. Are age, school year and major worth keeping at all, given their low weights?
3. Which is the best cross-term link for D4, and what are its risks?
4. Is a keyed hash of an email really pseudonymous here? What could re-identify someone even without names?
5. What are the risks of free-text fields (roommate requests, other requests), and how should they be handled?
6. What does a minimal but adequate login and role model look like for a single-computer, local app?
7. Is full-disk encryption enough, or is database-level encryption worth the key management?
8. What should the purge delete (including review-queue copies, notes and history), and how can we prove it ran?
9. What questions to the Lodge are missing from section 11?
10. What could go wrong with logs, crash dumps, browser storage or backups that this draft overlooks?
