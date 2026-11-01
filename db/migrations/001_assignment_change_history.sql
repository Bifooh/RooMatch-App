-- Incremental migration: assignment change history for undo.
-- Safe to re-run (IF NOT EXISTS). Does NOT drop data.
--
-- Apply (example against lodge_small on the Mac user-space socket):
--   /usr/local/mysql/bin/mysql -uroot --socket="$HOME/mysql-lodge/run/mysql.sock" lodge_small \
--     < db/migrations/001_assignment_change_history.sql
--
-- Or: mysql lodge_small < db/migrations/001_assignment_change_history.sql

SET NAMES utf8mb4;

CREATE TABLE IF NOT EXISTS assignment_change_history (
  history_id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  draft_id              INT UNSIGNED NOT NULL,
  term_id               INT UNSIGNED NOT NULL,
  change_type           ENUM('move','unassign','swap','lock','unlock','undo') NOT NULL,
  performed_by          VARCHAR(60)  NOT NULL COMMENT 'Fictional operator identity (no auth yet)',
  reason                VARCHAR(255) NULL COMMENT 'Optional; not required to edit',
  before_state          JSON NOT NULL COMMENT 'Assignment snapshots before the change (enough to undo)',
  after_state           JSON NOT NULL COMMENT 'Assignment snapshots after the change',
  undoes_history_id     BIGINT UNSIGNED NULL COMMENT 'If change_type=undo, the history row this reverses',
  undone_by_history_id  BIGINT UNSIGNED NULL COMMENT 'Set when a later undo row reversed this change',
  created_at            TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (history_id),
  KEY ix_ach_draft_created (draft_id, created_at),
  KEY ix_ach_draft_undoable (draft_id, undone_by_history_id, created_at),
  CONSTRAINT fk_ach_draft FOREIGN KEY (draft_id) REFERENCES assignment_drafts (draft_id) ON DELETE CASCADE,
  CONSTRAINT fk_ach_term  FOREIGN KEY (term_id)  REFERENCES terms (term_id),
  CONSTRAINT fk_ach_undoes FOREIGN KEY (undoes_history_id) REFERENCES assignment_change_history (history_id),
  CONSTRAINT fk_ach_undone_by FOREIGN KEY (undone_by_history_id) REFERENCES assignment_change_history (history_id),
  CONSTRAINT ck_ach_undo_shape CHECK (
       (change_type = 'undo' AND undoes_history_id IS NOT NULL)
    OR (change_type <> 'undo' AND undoes_history_id IS NULL)
  )
) ENGINE=InnoDB COMMENT='Before/after assignment mutations for undo (not full event sourcing)';
