CREATE TABLE restore_events (
    restore_event_id TEXT PRIMARY KEY,
    backup_sha256 TEXT NOT NULL,
    backup_created_at_utc TEXT NOT NULL,
    restored_at_utc TEXT NOT NULL,
    consumed INTEGER NOT NULL DEFAULT 0 CHECK(consumed IN (0,1)),
    consumed_run_id INTEGER REFERENCES runs(run_id),
    CHECK(consumed = 1 OR consumed_run_id IS NULL)
);
CREATE TABLE run_execution_details (
    run_id INTEGER PRIMARY KEY REFERENCES runs(run_id),
    recorded_at_utc TEXT NOT NULL,
    elapsed_milliseconds INTEGER,
    received_records INTEGER NOT NULL,
    accepted_records INTEGER NOT NULL,
    excluded_records INTEGER NOT NULL,
    error_counts_json TEXT NOT NULL
);
CREATE INDEX execution_detail_expiry ON run_execution_details(recorded_at_utc);
CREATE TABLE operational_logs (
    log_id INTEGER PRIMARY KEY AUTOINCREMENT,
    recorded_at_utc TEXT NOT NULL,
    category TEXT NOT NULL CHECK(category IN ('BackupSucceeded','BackupFailed','RetentionSucceeded','RetentionFailed','RestoreApplied')),
    related_run_id INTEGER REFERENCES runs(run_id)
);
CREATE INDEX operational_log_expiry ON operational_logs(recorded_at_utc);
