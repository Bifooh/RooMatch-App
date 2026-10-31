-- =====================================================================
-- The Lodge tenant-assignment MVP  -  MySQL 8.0 schema
-- ---------------------------------------------------------------------
-- * ALL data used with this schema in development, demos and tests must be
--   FICTIONAL. Real resident data needs a separate security review.
-- * Tenant identity (tenants) is kept separate from everything that is
--   term-specific (contracts, preferences, roommate_requests,
--   placement_restrictions, assignments).
-- * Assignments are to APARTMENT NUMBERS ONLY. There are deliberately no
--   room or bed identifiers anywhere in this schema.
-- * Resident capacity is stored explicitly on floor_plans (with an optional
--   per-apartment override). It is never derived from bedroom count.
--
-- Usage (into an existing, empty database):
--   mysql -e "CREATE DATABASE lodge_mvp CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"
--   mysql lodge_mvp < db/schema.sql
-- Re-running this file DROPS and recreates every table (data is lost).
-- =====================================================================

SET NAMES utf8mb4;
SET FOREIGN_KEY_CHECKS = 0;
DROP TABLE IF EXISTS review_queue;
DROP TABLE IF EXISTS assignments;
DROP TABLE IF EXISTS assignment_drafts;
DROP TABLE IF EXISTS placement_restrictions;
DROP TABLE IF EXISTS roommate_requests;
DROP TABLE IF EXISTS preferences;
DROP TABLE IF EXISTS contracts;
DROP TABLE IF EXISTS tenants;
DROP TABLE IF EXISTS apartments;
DROP TABLE IF EXISTS floor_plans;
DROP TABLE IF EXISTS terms;
SET FOREIGN_KEY_CHECKS = 1;

-- ---------------------------------------------------------------------
-- terms: a contract period (e.g. "Winter 2027").
-- ---------------------------------------------------------------------
CREATE TABLE terms (
  term_id     INT UNSIGNED NOT NULL AUTO_INCREMENT,
  code        VARCHAR(20)  NOT NULL COMMENT 'Short code, e.g. W2027',
  name        VARCHAR(60)  NOT NULL,
  start_date  DATE         NOT NULL,
  end_date    DATE         NOT NULL,
  PRIMARY KEY (term_id),
  UNIQUE KEY uq_terms_code (code),
  CONSTRAINT ck_terms_dates CHECK (end_date > start_date)
) ENGINE=InnoDB COMMENT='Contract terms / semesters';

-- ---------------------------------------------------------------------
-- floor_plans: what a tenant contracts for. Capacity is explicit.
-- Plan names/bed/bath are the real Lodge plans (6 women's, 4 men's).
-- housing_category is 'women' or 'men' (confirmed by Samuel).
-- resident_capacity values in the seeds are PLACEHOLDER ASSUMPTIONS.
-- ---------------------------------------------------------------------
CREATE TABLE floor_plans (
  floor_plan_id            INT UNSIGNED NOT NULL AUTO_INCREMENT,
  code                     VARCHAR(20)  NOT NULL COMMENT 'Stable short code used by imports/UI',
  name                     VARCHAR(80)  NOT NULL,
  variant                  ENUM('standard','oversized','flat','standard_townhome','oversized_townhome') NOT NULL,
  bedrooms                 TINYINT UNSIGNED NOT NULL,
  bathrooms                DECIMAL(3,1) NOT NULL COMMENT 'e.g. 1.0, 2.5',
  housing_category         VARCHAR(30)  NOT NULL COMMENT 'Eligibility group, e.g. men / women',
  resident_capacity        TINYINT UNSIGNED NOT NULL COMMENT 'Max residents per apartment. Stored, NOT derived from bedrooms. Current values are PLACEHOLDER ASSUMPTIONS',
  capacity_is_assumption   BOOLEAN      NOT NULL DEFAULT TRUE COMMENT 'TRUE until staff confirm the number',
  notes                    VARCHAR(255) NULL,
  PRIMARY KEY (floor_plan_id),
  UNIQUE KEY uq_floor_plans_code (code),
  CONSTRAINT ck_fp_capacity  CHECK (resident_capacity BETWEEN 1 AND 20),
  CONSTRAINT ck_fp_bedrooms  CHECK (bedrooms BETWEEN 0 AND 10),
  CONSTRAINT ck_fp_bathrooms CHECK (bathrooms >= 0 AND bathrooms <= 10)
) ENGINE=InnoDB COMMENT='Floor plans (layout + eligibility + capacity)';

-- ---------------------------------------------------------------------
-- apartments: physical units, identified ONLY by apartment_number.
-- apartment_number is a 4-digit number whose digits do NOT encode the
-- floor, so floor is stored explicitly. "Nearby" for renewal moves means
-- same building + same floor, then an adjacent floor.
-- ---------------------------------------------------------------------
CREATE TABLE apartments (
  apartment_id       INT UNSIGNED NOT NULL AUTO_INCREMENT,
  apartment_number   VARCHAR(12)  NOT NULL COMMENT 'The only assignable identifier, e.g. 2420 (does not encode floor)',
  floor_plan_id      INT UNSIGNED NOT NULL,
  building           VARCHAR(20)  NOT NULL COMMENT 'The Lodge has two: Women''s and Men''s',
  floor              TINYINT      NOT NULL COMMENT 'Stored explicitly (1-4 at the Lodge)',
  capacity_override  TINYINT UNSIGNED NULL COMMENT 'Use instead of floor plan capacity when set (staff-confirmed)',
  override_reason    VARCHAR(255) NULL,
  is_active          BOOLEAN      NOT NULL DEFAULT TRUE,
  PRIMARY KEY (apartment_id),
  UNIQUE KEY uq_apartments_number (apartment_number),
  KEY ix_apartments_plan (floor_plan_id),
  KEY ix_apartments_location (building, floor),
  CONSTRAINT fk_apartments_plan FOREIGN KEY (floor_plan_id) REFERENCES floor_plans (floor_plan_id),
  CONSTRAINT ck_apt_floor CHECK (floor BETWEEN 0 AND 20),
  CONSTRAINT ck_apt_override CHECK (capacity_override IS NULL OR capacity_override BETWEEN 0 AND 20),
  CONSTRAINT ck_apt_override_reason CHECK (capacity_override IS NULL OR override_reason IS NOT NULL)
) ENGINE=InnoDB COMMENT='Apartments. No room or bed identifiers by design';

-- Effective capacity used by validation and the solver.
CREATE OR REPLACE VIEW v_apartment_capacity AS
SELECT a.apartment_id, a.apartment_number, a.floor_plan_id, fp.code AS floor_plan_code,
       fp.housing_category,
       COALESCE(a.capacity_override, fp.resident_capacity) AS effective_capacity
FROM apartments a
JOIN floor_plans fp ON fp.floor_plan_id = a.floor_plan_id
WHERE a.is_active;

-- ---------------------------------------------------------------------
-- tenants: identity only. Nothing term-specific lives here.
-- ---------------------------------------------------------------------
CREATE TABLE tenants (
  tenant_id     INT UNSIGNED NOT NULL AUTO_INCREMENT,
  tenant_code   VARCHAR(20)  NOT NULL COMMENT 'Property-facing tenant ID (fictional in dev)',
  first_name    VARCHAR(60)  NOT NULL,
  last_name     VARCHAR(60)  NOT NULL,
  PRIMARY KEY (tenant_id),
  UNIQUE KEY uq_tenants_code (tenant_code),
  KEY ix_tenants_name (last_name, first_name)
) ENGINE=InnoDB COMMENT='Tenant identity only';

-- ---------------------------------------------------------------------
-- contracts: one per tenant per term.
-- current_apartment_id = where a RENEWING tenant lives now (previous term).
-- stay_preference only applies to renewing tenants.
-- ---------------------------------------------------------------------
CREATE TABLE contracts (
  contract_id            INT UNSIGNED NOT NULL AUTO_INCREMENT,
  tenant_id              INT UNSIGNED NOT NULL,
  term_id                INT UNSIGNED NOT NULL,
  floor_plan_id          INT UNSIGNED NOT NULL COMMENT 'Contracted floor plan',
  tenant_type            ENUM('new','renewing') NOT NULL,
  current_apartment_id   INT UNSIGNED NULL COMMENT 'Renewals only',
  stay_preference        ENUM('stay','move','no_preference') NULL COMMENT 'Renewals only',
  created_at             TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (contract_id),
  UNIQUE KEY uq_contracts_tenant_term (tenant_id, term_id),
  UNIQUE KEY uq_contracts_id_term (contract_id, term_id) COMMENT 'Target for composite FKs',
  KEY ix_contracts_term_plan (term_id, floor_plan_id),
  CONSTRAINT fk_contracts_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (tenant_id),
  CONSTRAINT fk_contracts_term   FOREIGN KEY (term_id)   REFERENCES terms (term_id),
  CONSTRAINT fk_contracts_plan   FOREIGN KEY (floor_plan_id) REFERENCES floor_plans (floor_plan_id),
  CONSTRAINT fk_contracts_current_apt FOREIGN KEY (current_apartment_id) REFERENCES apartments (apartment_id),
  CONSTRAINT ck_contracts_renewal_fields CHECK (
       (tenant_type = 'new'      AND current_apartment_id IS NULL AND stay_preference IS NULL)
    OR (tenant_type = 'renewing' AND current_apartment_id IS NOT NULL AND stay_preference IS NOT NULL)
  )
) ENGINE=InnoDB COMMENT='Term-specific contract';

-- ---------------------------------------------------------------------
-- preferences: per contract (term-specific). Animal/allergy fields are
-- hard-ish constraints; the rest are soft preferences. Lower-priority
-- optional fields are nullable and must never be required.
-- ---------------------------------------------------------------------
CREATE TABLE preferences (
  contract_id                  INT UNSIGNED NOT NULL,
  sleep_schedule               ENUM('early','typical','late','unknown') NOT NULL DEFAULT 'unknown',
  has_animal                   BOOLEAN NOT NULL DEFAULT FALSE COMMENT 'An animal will live in the apartment',
  animal_allergy_restriction   BOOLEAN NOT NULL DEFAULT FALSE COMMENT 'Cannot live with an animal',
  -- Optional, lower priority. Similar values do NOT imply better roommates.
  age                          TINYINT UNSIGNED NULL,
  social_preference            ENUM('outgoing','balanced','introverted') NULL,
  major                        VARCHAR(80) NULL,
  academic_stage               ENUM('first_year','second_year','third_year','fourth_year_plus','graduate') NULL,
  PRIMARY KEY (contract_id),
  CONSTRAINT fk_preferences_contract FOREIGN KEY (contract_id) REFERENCES contracts (contract_id) ON DELETE CASCADE,
  CONSTRAINT ck_pref_age CHECK (age IS NULL OR age BETWEEN 16 AND 99)
) ENGINE=InnoDB COMMENT='Per-contract preferences and animal/allergy facts';

-- ---------------------------------------------------------------------
-- roommate_requests: one row per person named by the requester.
-- raw_text is the ORIGINAL wording and is never overwritten.
-- Resolution (matching raw_text to a tenant) is done by the app; until
-- then status = 'pending' and resolved_tenant_id is NULL.
-- ---------------------------------------------------------------------
CREATE TABLE roommate_requests (
  request_id           INT UNSIGNED NOT NULL AUTO_INCREMENT,
  contract_id          INT UNSIGNED NOT NULL COMMENT 'Requester contract',
  raw_text             VARCHAR(255) NOT NULL COMMENT 'Original wording as submitted',
  resolved_tenant_id   INT UNSIGNED NULL COMMENT 'Set only when the name is confidently resolved',
  status               ENUM('pending','resolved','unresolved','ambiguous','excluded') NOT NULL DEFAULT 'pending',
  exclusion_reason     VARCHAR(500) NULL COMMENT 'Why it was not used for matching',
  PRIMARY KEY (request_id),
  KEY ix_rr_contract (contract_id),
  KEY ix_rr_resolved (resolved_tenant_id),
  CONSTRAINT fk_rr_contract FOREIGN KEY (contract_id) REFERENCES contracts (contract_id) ON DELETE CASCADE,
  CONSTRAINT fk_rr_resolved FOREIGN KEY (resolved_tenant_id) REFERENCES tenants (tenant_id),
  CONSTRAINT ck_rr_resolved CHECK (status <> 'resolved' OR resolved_tenant_id IS NOT NULL),
  CONSTRAINT ck_rr_reason   CHECK (status NOT IN ('unresolved','ambiguous','excluded') OR exclusion_reason IS NOT NULL)
) ENGINE=InnoDB COMMENT='Roommate requests with original wording';

-- ---------------------------------------------------------------------
-- placement_restrictions: STAFF-CONFIRMED hard rules for one contract.
-- Store only the rule, not the confidential reason behind it.
-- ---------------------------------------------------------------------
CREATE TABLE placement_restrictions (
  restriction_id      INT UNSIGNED NOT NULL AUTO_INCREMENT,
  contract_id         INT UNSIGNED NOT NULL,
  restriction_type    ENUM('required_apartment','excluded_apartment','required_floor',
                           'excluded_floor','must_not_share_with') NOT NULL,
  apartment_id        INT UNSIGNED NULL COMMENT 'For required_/excluded_apartment',
  other_tenant_id     INT UNSIGNED NULL COMMENT 'For must_not_share_with',
  value               VARCHAR(30)  NULL COMMENT 'For required_floor / excluded_floor',
  description         VARCHAR(255) NOT NULL COMMENT 'Non-confidential wording only',
  confirmed_by        VARCHAR(60)  NOT NULL COMMENT 'Staff member (fictional in dev)',
  confirmed_at        DATETIME     NOT NULL,
  PRIMARY KEY (restriction_id),
  KEY ix_pr_contract (contract_id),
  CONSTRAINT fk_pr_contract FOREIGN KEY (contract_id) REFERENCES contracts (contract_id) ON DELETE CASCADE,
  CONSTRAINT fk_pr_apartment FOREIGN KEY (apartment_id) REFERENCES apartments (apartment_id),
  CONSTRAINT fk_pr_other_tenant FOREIGN KEY (other_tenant_id) REFERENCES tenants (tenant_id),
  CONSTRAINT ck_pr_target CHECK (
       (restriction_type IN ('required_apartment','excluded_apartment') AND apartment_id IS NOT NULL)
    OR (restriction_type = 'must_not_share_with' AND other_tenant_id IS NOT NULL)
    OR (restriction_type IN ('required_floor','excluded_floor') AND value IS NOT NULL)
  )
) ENGINE=InnoDB COMMENT='Staff-confirmed placement restrictions';

-- ---------------------------------------------------------------------
-- assignment_drafts: a full or partial plan for one term.
-- priority_config holds the adjustable priority order/weights used when a
-- draft was generated (not finalized yet, so free-form JSON).
-- ---------------------------------------------------------------------
CREATE TABLE assignment_drafts (
  draft_id         INT UNSIGNED NOT NULL AUTO_INCREMENT,
  term_id          INT UNSIGNED NOT NULL,
  name             VARCHAR(100) NOT NULL,
  source           ENUM('manual','generated','imported') NOT NULL,
  status           ENUM('draft','final','archived') NOT NULL DEFAULT 'draft',
  priority_config  JSON NULL,
  notes            VARCHAR(500) NULL,
  created_at       TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at       TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (draft_id),
  UNIQUE KEY uq_drafts_id_term (draft_id, term_id) COMMENT 'Target for composite FKs',
  KEY ix_drafts_term (term_id),
  CONSTRAINT fk_drafts_term FOREIGN KEY (term_id) REFERENCES terms (term_id)
) ENGINE=InnoDB COMMENT='Saved assignment plans (drafts)';

-- ---------------------------------------------------------------------
-- assignments: contract -> apartment within one draft.
-- The composite FKs guarantee the contract and the draft are for the same
-- term. Capacity, eligibility and restriction checks are done by the
-- shared C# rules engine, not by triggers, so the UI can show *why*.
-- ---------------------------------------------------------------------
CREATE TABLE assignments (
  assignment_id   INT UNSIGNED NOT NULL AUTO_INCREMENT,
  draft_id        INT UNSIGNED NOT NULL,
  term_id         INT UNSIGNED NOT NULL,
  contract_id     INT UNSIGNED NOT NULL,
  apartment_id    INT UNSIGNED NOT NULL,
  is_locked       BOOLEAN NOT NULL DEFAULT FALSE COMMENT 'Manager-locked: solver must preserve',
  locked_by       VARCHAR(60) NULL,
  locked_at       DATETIME NULL,
  source          ENUM('manual','generated','imported','carried_over') NOT NULL,
  explanation     TEXT NULL COMMENT 'Human-readable reason; not a claim of optimality',
  updated_at      TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (assignment_id),
  UNIQUE KEY uq_assignments_draft_contract (draft_id, contract_id) COMMENT 'One apartment per contract per draft',
  KEY ix_assignments_draft_apt (draft_id, apartment_id),
  CONSTRAINT fk_asg_draft    FOREIGN KEY (draft_id, term_id)    REFERENCES assignment_drafts (draft_id, term_id) ON DELETE CASCADE,
  CONSTRAINT fk_asg_contract FOREIGN KEY (contract_id, term_id) REFERENCES contracts (contract_id, term_id),
  CONSTRAINT fk_asg_apartment FOREIGN KEY (apartment_id) REFERENCES apartments (apartment_id),
  CONSTRAINT ck_asg_lock CHECK (is_locked = FALSE OR locked_by IS NOT NULL)
) ENGINE=InnoDB COMMENT='Contract to apartment assignments within a draft';

-- ---------------------------------------------------------------------
-- review_queue: things a human must look at. Keeps original wording and
-- the reason, never invented data.
-- ---------------------------------------------------------------------
CREATE TABLE review_queue (
  item_id              INT UNSIGNED NOT NULL AUTO_INCREMENT,
  term_id              INT UNSIGNED NOT NULL,
  draft_id             INT UNSIGNED NULL,
  contract_id          INT UNSIGNED NULL,
  roommate_request_id  INT UNSIGNED NULL,
  item_type            ENUM('unresolved_roommate_request','ambiguous_roommate_request',
                            'non_mutual_request','group_exceeds_capacity','animal_allergy_conflict',
                            'floor_plan_oversubscribed','renewal_floor_plan_changed',
                            'restriction_conflict','missing_data','other') NOT NULL,
  original_wording     VARCHAR(500) NULL,
  reason               VARCHAR(1000) NOT NULL,
  status               ENUM('open','resolved','dismissed') NOT NULL DEFAULT 'open',
  resolution_note      VARCHAR(500) NULL,
  created_at           TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (item_id),
  KEY ix_rq_term_status (term_id, status),
  CONSTRAINT fk_rq_term     FOREIGN KEY (term_id)     REFERENCES terms (term_id),
  CONSTRAINT fk_rq_draft    FOREIGN KEY (draft_id)    REFERENCES assignment_drafts (draft_id) ON DELETE CASCADE,
  CONSTRAINT fk_rq_contract FOREIGN KEY (contract_id) REFERENCES contracts (contract_id) ON DELETE CASCADE,
  CONSTRAINT fk_rq_request  FOREIGN KEY (roommate_request_id) REFERENCES roommate_requests (request_id) ON DELETE CASCADE
) ENGINE=InnoDB COMMENT='Human review queue';
