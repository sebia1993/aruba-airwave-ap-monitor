CREATE TABLE dataset (
    dataset_id TEXT PRIMARY KEY,
    mode INTEGER NOT NULL,
    schema_version INTEGER NOT NULL CHECK(schema_version > 0)
);
CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY CHECK(version > 0),
    applied_at_utc TEXT NOT NULL
);
CREATE TABLE profiles (
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    configuration_version INTEGER NOT NULL CHECK(configuration_version > 0),
    profile_json TEXT NOT NULL,
    PRIMARY KEY(dataset_id, configuration_version)
);
CREATE TABLE scopes (
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    scope_version INTEGER NOT NULL CHECK(scope_version > 0),
    instance_id TEXT NOT NULL,
    origin TEXT NOT NULL,
    baseline_status INTEGER NOT NULL,
    target_universe_known INTEGER NOT NULL CHECK(target_universe_known IN (0,1)),
    PRIMARY KEY(dataset_id, scope_version)
);
CREATE TABLE expected_targets (
    dataset_id TEXT NOT NULL,
    scope_version INTEGER NOT NULL,
    source_ap_id TEXT NOT NULL,
    PRIMARY KEY(dataset_id, scope_version, source_ap_id),
    FOREIGN KEY(dataset_id, scope_version) REFERENCES scopes(dataset_id, scope_version)
);
CREATE TABLE runs (
    run_id INTEGER PRIMARY KEY AUTOINCREMENT,
    dataset_id TEXT NOT NULL,
    scope_version INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    lifecycle INTEGER NOT NULL CHECK(lifecycle IN (0,1,2)),
    context_json TEXT NOT NULL,
    target_universe_known INTEGER NOT NULL CHECK(target_universe_known IN (0,1)),
    FOREIGN KEY(dataset_id, scope_version) REFERENCES scopes(dataset_id, scope_version)
);
CREATE UNIQUE INDEX one_running_ap_run ON runs(dataset_id) WHERE kind=0 AND lifecycle=0;
CREATE TABLE run_results (
    run_id INTEGER PRIMARY KEY REFERENCES runs(run_id),
    dataset_id TEXT NOT NULL,
    kind INTEGER NOT NULL,
    finalized_at_utc TEXT NOT NULL,
    outcome INTEGER NOT NULL,
    receipt_json TEXT NOT NULL,
    completeness INTEGER NOT NULL,
    errors_json TEXT NOT NULL,
    source_evidence_json TEXT NOT NULL,
    UNIQUE(dataset_id,kind,run_id)
);
CREATE TABLE aps (
    ap_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    instance_id TEXT NOT NULL,
    source_ap_id TEXT NOT NULL,
    UNIQUE(dataset_id,instance_id,source_ap_id)
);
CREATE TABLE generations (
    generation_id TEXT PRIMARY KEY,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    started_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ended_run_id INTEGER REFERENCES runs(run_id),
    replacement_needs_confirmation INTEGER NOT NULL CHECK(replacement_needs_confirmation IN (0,1))
);
CREATE TABLE monitoring_intervals (
    interval_id TEXT PRIMARY KEY,
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    started_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ended_run_id INTEGER REFERENCES runs(run_id),
    end_reason TEXT
);
CREATE TABLE metadata_versions (
    metadata_version_id INTEGER PRIMARY KEY AUTOINCREMENT,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    metadata_json TEXT NOT NULL,
    UNIQUE(ap_id,generation_id,metadata_json)
);
CREATE TABLE ap_current (
    ap_id TEXT PRIMARY KEY REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    metadata_version_id INTEGER NOT NULL REFERENCES metadata_versions(metadata_version_id),
    last_processed_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    tracking_json TEXT NOT NULL,
    fields_json TEXT NOT NULL,
    source_at_utc TEXT,
    last_contacted_at_utc TEXT,
    measurements_json TEXT NOT NULL
);
CREATE TABLE incidents (
    incident_id TEXT PRIMARY KEY,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    is_open INTEGER NOT NULL CHECK(is_open IN (0,1)),
    tracking_json TEXT NOT NULL
);
CREATE UNIQUE INDEX one_open_incident_per_generation ON incidents(generation_id) WHERE is_open=1;
CREATE TABLE incident_snapshots (
    incident_id TEXT NOT NULL REFERENCES incidents(incident_id),
    snapshot_kind INTEGER NOT NULL CHECK(snapshot_kind IN (0,1)),
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    snapshot_json TEXT NOT NULL,
    PRIMARY KEY(incident_id,snapshot_kind)
);
CREATE TABLE observation_gaps (
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    first_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    gap_json TEXT NOT NULL,
    PRIMARY KEY(ap_id,generation_id,interval_id,first_run_id)
);
CREATE TABLE run_rows (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    metadata_version_id INTEGER NOT NULL REFERENCES metadata_versions(metadata_version_id),
    facts_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE run_transitions (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    incident_id TEXT NOT NULL REFERENCES incidents(incident_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    mutation_kind INTEGER NOT NULL,
    tracking_json TEXT NOT NULL,
    PRIMARY KEY(run_id,incident_id,mutation_kind)
);
CREATE TABLE metadata_changes (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    before_version_id INTEGER REFERENCES metadata_versions(metadata_version_id),
    after_version_id INTEGER NOT NULL REFERENCES metadata_versions(metadata_version_id),
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE monitoring_changes (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    status INTEGER NOT NULL,
    reason TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE success_times (
    dataset_id TEXT NOT NULL,
    scope_version INTEGER NOT NULL,
    kind INTEGER NOT NULL,
    last_valid_run_id INTEGER REFERENCES runs(run_id),
    last_valid_committed_at_utc TEXT,
    last_complete_run_id INTEGER REFERENCES runs(run_id),
    last_complete_committed_at_utc TEXT,
    PRIMARY KEY(dataset_id,scope_version,kind),
    FOREIGN KEY(dataset_id,scope_version) REFERENCES scopes(dataset_id,scope_version)
);
CREATE TRIGGER finalized_runs_are_immutable BEFORE UPDATE ON runs WHEN OLD.lifecycle != 0
BEGIN SELECT RAISE(ABORT,'Finalized run is immutable'); END;
CREATE TRIGGER finalized_results_are_immutable BEFORE UPDATE ON run_results
BEGIN SELECT RAISE(ABORT,'Final result is immutable'); END;
CREATE TRIGGER run_rows_are_immutable BEFORE UPDATE ON run_rows
BEGIN SELECT RAISE(ABORT,'Historical row is immutable'); END;
CREATE TRIGGER metadata_versions_are_immutable BEFORE UPDATE ON metadata_versions
BEGIN SELECT RAISE(ABORT,'Metadata version is immutable'); END;
CREATE TRIGGER incident_snapshots_are_immutable BEFORE UPDATE ON incident_snapshots
BEGIN SELECT RAISE(ABORT,'Incident snapshot is immutable'); END;
CREATE TRIGGER run_transitions_are_immutable BEFORE UPDATE ON run_transitions
BEGIN SELECT RAISE(ABORT,'Transition is immutable'); END;
