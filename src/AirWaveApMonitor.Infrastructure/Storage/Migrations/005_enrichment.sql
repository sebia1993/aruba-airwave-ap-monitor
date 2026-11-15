CREATE TABLE collection_commits (
    commit_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL UNIQUE REFERENCES runs(run_id)
);
-- Before schema 5 only AP runs could finalize; their one-running-AP constraint serialized commits.
INSERT INTO collection_commits(run_id) SELECT run_id FROM run_results ORDER BY run_id;
CREATE UNIQUE INDEX one_running_enrichment_kind ON runs(dataset_id,kind) WHERE kind IN (1,2) AND lifecycle=0;
CREATE TABLE enrichment_run_bindings (
    run_id INTEGER PRIMARY KEY REFERENCES runs(run_id),
    profile_key TEXT NOT NULL,
    profile_json TEXT NOT NULL,
    restore_epoch INTEGER NOT NULL,
    connection_record_ids_json TEXT NOT NULL
);
CREATE INDEX enrichment_binding_profile ON enrichment_run_bindings(profile_key,run_id);
CREATE TABLE enrichment_target_bindings (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    target_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE enrichment_connections (
    connection_id TEXT PRIMARY KEY,
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    profile_key TEXT NOT NULL,
    local_key TEXT NOT NULL,
    remote_key TEXT NOT NULL,
    read_at_utc TEXT NOT NULL,
    source_at_utc TEXT,
    observation_json TEXT NOT NULL
);
CREATE INDEX enrichment_connection_endpoint ON enrichment_connections(profile_key,local_key,remote_key,run_id);
CREATE TABLE enrichment_records (
    record_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    record_type INTEGER NOT NULL CHECK(record_type IN (0,1,2)),
    profile_key TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    source_id TEXT,
    observed_at_utc TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    state_json TEXT NOT NULL,
    has_gap INTEGER NOT NULL CHECK(has_gap IN (0,1)),
    UNIQUE(record_type,source_id)
);
CREATE INDEX enrichment_record_run ON enrichment_records(run_id,record_type,record_id);
CREATE TABLE enrichment_record_targets (
    record_id INTEGER NOT NULL REFERENCES enrichment_records(record_id),
    ordinal INTEGER NOT NULL,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    point_id TEXT REFERENCES installation_points(point_id),
    PRIMARY KEY(record_id,ordinal)
);
CREATE INDEX enrichment_target_history ON enrichment_record_targets(ap_id,generation_id,interval_id,record_id);
CREATE TABLE enrichment_current (
    profile_key TEXT NOT NULL,
    record_type INTEGER NOT NULL,
    entity_key TEXT NOT NULL,
    record_id INTEGER NOT NULL REFERENCES enrichment_records(record_id),
    PRIMARY KEY(profile_key,record_type,entity_key)
);
CREATE TABLE power_samples (
    sample_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    sample_id TEXT NOT NULL UNIQUE,
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    profile_key TEXT NOT NULL,
    scope_version INTEGER NOT NULL,
    endpoint_role INTEGER NOT NULL,
    source_device_id TEXT NOT NULL,
    source_interface_id TEXT,
    metric INTEGER NOT NULL,
    ap_id TEXT REFERENCES aps(ap_id),
    generation_id TEXT REFERENCES generations(generation_id),
    interval_id TEXT REFERENCES monitoring_intervals(interval_id),
    point_id TEXT REFERENCES installation_points(point_id),
    read_at_utc TEXT NOT NULL,
    source_at_utc TEXT,
    sample_json TEXT NOT NULL,
    normalized_json TEXT NOT NULL,
    attribution_json TEXT NOT NULL
);
CREATE INDEX power_profile_time ON power_samples(profile_key,read_at_utc,sample_sequence);
CREATE INDEX power_target_time ON power_samples(ap_id,generation_id,read_at_utc,sample_sequence);
CREATE INDEX power_endpoint_time ON power_samples(source_device_id,source_interface_id,metric,read_at_utc,sample_sequence);
CREATE TABLE power_sample_keys (
    key_hash TEXT PRIMARY KEY,
    measurement_hash TEXT NOT NULL,
    sample_sequence INTEGER REFERENCES power_samples(sample_sequence) ON DELETE SET NULL
);
CREATE TABLE enrichment_power_reads (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ordinal INTEGER NOT NULL,
    sample_sequence INTEGER REFERENCES power_samples(sample_sequence) ON DELETE SET NULL,
    read_at_utc TEXT NOT NULL,
    PRIMARY KEY(run_id,ordinal)
);
CREATE TABLE enrichment_power_current (
    profile_key TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    sample_sequence INTEGER REFERENCES power_samples(sample_sequence) ON DELETE SET NULL,
    PRIMARY KEY(profile_key,entity_key)
);
CREATE TABLE enrichment_run_stats (
    run_id INTEGER PRIMARY KEY REFERENCES runs(run_id),
    profile_key TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    disposition INTEGER NOT NULL,
    connection_count INTEGER NOT NULL,
    port_count INTEGER NOT NULL,
    power_count INTEGER NOT NULL,
    state_count INTEGER NOT NULL,
    valid_count INTEGER NOT NULL,
    has_gap INTEGER NOT NULL
);
CREATE TABLE enrichment_gaps (
    gap_id TEXT PRIMARY KEY,
    profile_key TEXT NOT NULL,
    kind INTEGER NOT NULL,
    first_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    first_missing_at_utc TEXT NOT NULL,
    reason INTEGER NOT NULL,
    current_version INTEGER NOT NULL,
    is_open INTEGER NOT NULL CHECK(is_open IN (0,1)),
    last_detail_at_utc TEXT NOT NULL
);
CREATE UNIQUE INDEX one_open_enrichment_gap ON enrichment_gaps(profile_key,kind) WHERE is_open=1;
CREATE TABLE enrichment_gap_versions (
    gap_id TEXT NOT NULL REFERENCES enrichment_gaps(gap_id),
    version INTEGER NOT NULL,
    recorded_at_utc TEXT NOT NULL,
    view_json TEXT NOT NULL,
    PRIMARY KEY(gap_id,version)
);
CREATE TABLE enrichment_snapshot_refs (
    ref_id INTEGER PRIMARY KEY AUTOINCREMENT,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    profile_key TEXT NOT NULL,
    refs_hash TEXT NOT NULL,
    references_json TEXT NOT NULL,
    UNIQUE(ap_id,generation_id,interval_id,profile_key,refs_hash)
);
CREATE TABLE enrichment_retention_state (key TEXT PRIMARY KEY,cutoff_utc TEXT NOT NULL);
CREATE TABLE enrichment_restore_events (
    restore_event_id TEXT NOT NULL REFERENCES restore_events(restore_event_id),
    kind INTEGER NOT NULL,
    consumed_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    PRIMARY KEY(restore_event_id,kind)
);
CREATE TRIGGER collection_commits_immutable BEFORE UPDATE ON collection_commits BEGIN SELECT RAISE(ABORT,'Commit order is immutable'); END;
CREATE TRIGGER collection_commits_no_delete BEFORE DELETE ON collection_commits BEGIN SELECT RAISE(ABORT,'Commit order is permanent'); END;
CREATE TRIGGER enrichment_bindings_immutable BEFORE UPDATE ON enrichment_run_bindings BEGIN SELECT RAISE(ABORT,'Run binding is immutable'); END;
CREATE TRIGGER enrichment_targets_immutable BEFORE UPDATE ON enrichment_target_bindings BEGIN SELECT RAISE(ABORT,'Target binding is immutable'); END;
CREATE TRIGGER enrichment_connections_immutable BEFORE UPDATE ON enrichment_connections BEGIN SELECT RAISE(ABORT,'Connection evidence is immutable'); END;
CREATE TRIGGER enrichment_records_immutable BEFORE UPDATE ON enrichment_records BEGIN SELECT RAISE(ABORT,'Enrichment evidence is immutable'); END;
CREATE TRIGGER enrichment_record_targets_immutable BEFORE UPDATE ON enrichment_record_targets BEGIN SELECT RAISE(ABORT,'Evidence targets are immutable'); END;
CREATE TRIGGER power_samples_immutable BEFORE UPDATE ON power_samples BEGIN SELECT RAISE(ABORT,'Power sample is immutable'); END;
CREATE TRIGGER enrichment_stats_immutable BEFORE UPDATE ON enrichment_run_stats BEGIN SELECT RAISE(ABORT,'Run result is immutable'); END;
CREATE TRIGGER enrichment_gap_versions_immutable BEFORE UPDATE ON enrichment_gap_versions BEGIN SELECT RAISE(ABORT,'Gap version is immutable'); END;
CREATE TRIGGER enrichment_snapshot_refs_immutable BEFORE UPDATE ON enrichment_snapshot_refs BEGIN SELECT RAISE(ABORT,'Snapshot reference is immutable'); END;
CREATE TRIGGER enrichment_snapshot_refs_no_delete BEFORE DELETE ON enrichment_snapshot_refs BEGIN SELECT RAISE(ABORT,'Snapshot reference is permanent'); END;

-- Accepted pointer history preserves the decision made at apply, including partial field merges.
-- Raw valid-looking records rejected for source regression/historical-only disposition are absent.
CREATE TABLE enrichment_accepted_records (
    record_id INTEGER PRIMARY KEY REFERENCES enrichment_records(record_id),
    profile_key TEXT NOT NULL,
    record_type INTEGER NOT NULL,
    entity_key TEXT NOT NULL,
    run_id INTEGER NOT NULL REFERENCES runs(run_id)
);
CREATE INDEX enrichment_accepted_record_selection ON enrichment_accepted_records(profile_key,record_type,entity_key,run_id,record_id);
CREATE TABLE enrichment_accepted_power (
    accepted_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_key TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    -- Permanent numeric reference/tombstone, intentionally not a cascading FK: samples expire.
    sample_sequence INTEGER NOT NULL,
    ap_id TEXT REFERENCES aps(ap_id),
    generation_id TEXT REFERENCES generations(generation_id),
    interval_id TEXT REFERENCES monitoring_intervals(interval_id)
);
CREATE INDEX enrichment_accepted_power_selection ON enrichment_accepted_power(profile_key,entity_key,run_id,accepted_sequence);
CREATE INDEX enrichment_accepted_power_target ON enrichment_accepted_power(ap_id,generation_id,interval_id,profile_key,entity_key);
CREATE TRIGGER enrichment_accepted_records_immutable BEFORE UPDATE ON enrichment_accepted_records BEGIN SELECT RAISE(ABORT,'Accepted evidence history is immutable'); END;
CREATE TRIGGER enrichment_accepted_records_no_delete BEFORE DELETE ON enrichment_accepted_records BEGIN SELECT RAISE(ABORT,'Accepted evidence history is permanent'); END;
CREATE TRIGGER enrichment_accepted_power_immutable BEFORE UPDATE ON enrichment_accepted_power BEGIN SELECT RAISE(ABORT,'Accepted power history is immutable'); END;
CREATE TRIGGER enrichment_accepted_power_no_delete BEFORE DELETE ON enrichment_accepted_power BEGIN SELECT RAISE(ABORT,'Accepted power history is permanent'); END;
