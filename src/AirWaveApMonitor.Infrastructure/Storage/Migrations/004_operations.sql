CREATE TABLE installation_points (
    point_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    inventory_key TEXT,
    current_version INTEGER NOT NULL,
    UNIQUE(dataset_id,inventory_key)
);
CREATE TABLE installation_point_versions (
    point_id TEXT NOT NULL REFERENCES installation_points(point_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    PRIMARY KEY(point_id,version)
);
CREATE TABLE manual_inventory_versions (
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    version INTEGER NOT NULL,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    version_json TEXT NOT NULL,
    PRIMARY KEY(generation_id,version)
);
CREATE TABLE manual_inventory_current (
    generation_id TEXT PRIMARY KEY REFERENCES generations(generation_id),
    version INTEGER NOT NULL,
    FOREIGN KEY(generation_id,version) REFERENCES manual_inventory_versions(generation_id,version)
);
CREATE TABLE tracking_policy_versions (
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    source INTEGER NOT NULL,
    PRIMARY KEY(ap_id,version)
);
CREATE TABLE tracking_policy_current (
    ap_id TEXT PRIMARY KEY REFERENCES aps(ap_id),
    version INTEGER NOT NULL,
    FOREIGN KEY(ap_id,version) REFERENCES tracking_policy_versions(ap_id,version)
);
CREATE TABLE placements (
    placement_id TEXT PRIMARY KEY,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    point_id TEXT NOT NULL REFERENCES installation_points(point_id),
    current_version INTEGER NOT NULL
);
CREATE INDEX placements_point ON placements(point_id,generation_id);
CREATE TABLE placement_versions (
    placement_id TEXT NOT NULL REFERENCES placements(placement_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    PRIMARY KEY(placement_id,version)
);
CREATE TABLE placement_current (
    generation_id TEXT PRIMARY KEY REFERENCES generations(generation_id),
    version INTEGER NOT NULL,
    placement_id TEXT REFERENCES placements(placement_id)
);
CREATE TABLE work_items (
    work_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    current_version INTEGER NOT NULL
);
CREATE TABLE work_versions (
    work_id TEXT NOT NULL REFERENCES work_items(work_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    PRIMARY KEY(work_id,version)
);
CREATE TABLE work_targets (
    work_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    ap_id TEXT REFERENCES aps(ap_id),
    generation_id TEXT REFERENCES generations(generation_id),
    incident_id TEXT REFERENCES incidents(incident_id),
    point_id TEXT REFERENCES installation_points(point_id),
    PRIMARY KEY(work_id,version,ordinal),
    FOREIGN KEY(work_id,version) REFERENCES work_versions(work_id,version)
);
CREATE TABLE operation_events (
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    kind INTEGER NOT NULL,
    recorded_at_utc TEXT NOT NULL,
    origin_key TEXT NOT NULL UNIQUE,
    run_id INTEGER REFERENCES runs(run_id),
    event_json TEXT NOT NULL
);
CREATE INDEX operation_event_filter ON operation_events(dataset_id,kind,recorded_at_utc,event_id);
CREATE TABLE operation_event_targets (
    event_id INTEGER NOT NULL REFERENCES operation_events(event_id),
    ordinal INTEGER NOT NULL,
    ap_id TEXT REFERENCES aps(ap_id),
    generation_id TEXT REFERENCES generations(generation_id),
    incident_id TEXT REFERENCES incidents(incident_id),
    point_id TEXT REFERENCES installation_points(point_id),
    PRIMARY KEY(event_id,ordinal)
);
CREATE INDEX operation_target_ap ON operation_event_targets(ap_id,generation_id,event_id);
CREATE INDEX operation_target_point ON operation_event_targets(point_id,event_id);
CREATE TABLE operation_commands (
    command_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    kind TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    result_json TEXT NOT NULL,
    recorded_at_utc TEXT NOT NULL
);
CREATE TABLE import_previews (
    preview_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    preview_json TEXT NOT NULL,
    placement_versions_json TEXT NOT NULL,
    recorded_at_utc TEXT NOT NULL
);
CREATE TABLE run_manual_bindings (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    binding_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE operations_applied_bindings (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    binding_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE operations_restore_epochs (
    epoch INTEGER PRIMARY KEY AUTOINCREMENT,
    restore_event_id TEXT NOT NULL UNIQUE REFERENCES restore_events(restore_event_id)
);
CREATE TABLE operations_state (key TEXT PRIMARY KEY,value INTEGER NOT NULL);
CREATE INDEX run_rows_ap_run ON run_rows(ap_id,run_id);
CREATE TRIGGER point_version_immutable BEFORE UPDATE ON installation_point_versions BEGIN SELECT RAISE(ABORT,'Point version is immutable'); END;
CREATE TRIGGER inventory_version_immutable BEFORE UPDATE ON manual_inventory_versions BEGIN SELECT RAISE(ABORT,'Inventory version is immutable'); END;
CREATE TRIGGER policy_version_immutable BEFORE UPDATE ON tracking_policy_versions BEGIN SELECT RAISE(ABORT,'Policy version is immutable'); END;
CREATE TRIGGER placement_version_immutable BEFORE UPDATE ON placement_versions BEGIN SELECT RAISE(ABORT,'Placement version is immutable'); END;
CREATE TRIGGER work_version_immutable BEFORE UPDATE ON work_versions BEGIN SELECT RAISE(ABORT,'Work version is immutable'); END;
CREATE TRIGGER work_targets_immutable BEFORE UPDATE ON work_targets BEGIN SELECT RAISE(ABORT,'Work targets are immutable'); END;
CREATE TRIGGER operation_event_immutable BEFORE UPDATE ON operation_events BEGIN SELECT RAISE(ABORT,'Operation event is immutable'); END;
CREATE TRIGGER operation_targets_immutable BEFORE UPDATE ON operation_event_targets BEGIN SELECT RAISE(ABORT,'Operation targets are immutable'); END;
CREATE TRIGGER operation_receipt_immutable BEFORE UPDATE ON operation_commands BEGIN SELECT RAISE(ABORT,'Operation receipt is immutable'); END;
CREATE TRIGGER import_preview_immutable BEFORE UPDATE ON import_previews BEGIN SELECT RAISE(ABORT,'Import preview is immutable'); END;
CREATE TRIGGER run_bindings_immutable BEFORE UPDATE ON run_manual_bindings BEGIN SELECT RAISE(ABORT,'Run binding is immutable'); END;
CREATE TRIGGER applied_bindings_immutable BEFORE UPDATE ON operations_applied_bindings BEGIN SELECT RAISE(ABORT,'Applied binding is immutable'); END;

CREATE TABLE replacement_confirmations (
    replacement_event_id INTEGER PRIMARY KEY REFERENCES operation_events(event_id),
    confirmation_event_id INTEGER NOT NULL UNIQUE REFERENCES operation_events(event_id),
    work_id TEXT NOT NULL,
    work_version INTEGER NOT NULL,
    FOREIGN KEY(work_id,work_version) REFERENCES work_versions(work_id,version)
);
CREATE TRIGGER replacement_confirmation_immutable BEFORE UPDATE ON replacement_confirmations BEGIN SELECT RAISE(ABORT,'Replacement confirmation is immutable'); END;
CREATE TRIGGER installation_point_versions_no_delete BEFORE DELETE ON installation_point_versions BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER manual_inventory_versions_no_delete BEFORE DELETE ON manual_inventory_versions BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER tracking_policy_versions_no_delete BEFORE DELETE ON tracking_policy_versions BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER placement_versions_no_delete BEFORE DELETE ON placement_versions BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER work_versions_no_delete BEFORE DELETE ON work_versions BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER work_targets_no_delete BEFORE DELETE ON work_targets BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER operation_events_no_delete BEFORE DELETE ON operation_events BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER operation_event_targets_no_delete BEFORE DELETE ON operation_event_targets BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER operation_commands_no_delete BEFORE DELETE ON operation_commands BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER import_previews_no_delete BEFORE DELETE ON import_previews BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER run_manual_bindings_no_delete BEFORE DELETE ON run_manual_bindings BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER operations_applied_bindings_no_delete BEFORE DELETE ON operations_applied_bindings BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER operations_restore_epochs_no_delete BEFORE DELETE ON operations_restore_epochs BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
CREATE TRIGGER replacement_confirmations_no_delete BEFORE DELETE ON replacement_confirmations BEGIN SELECT RAISE(ABORT,'Operations history is permanent'); END;
