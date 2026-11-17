-- Synthetic schema5 fixture produced by the pre-correction real SQLite store (bc4cd3 product DLL).
BEGIN TRANSACTION;
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
INSERT INTO "ap_current" VALUES('64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4',3,2,'{"Identity":{"InstanceId":"__INSTANCE__","DatasetId":"__DATASET__","SourceApId":"100","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","Identity":{"SerialNumber":"SERIAL-100","WiredMac":null},"Fields":{"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}}},"MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","MonitoringStatus":0,"LastValidState":0,"LastValidRunId":2,"LastValidObservedAtUtc":"2026-11-15T01:05:00+00:00","LastUpAtUtc":"2026-11-15T01:05:00+00:00","OpenIncident":null,"OpenGap":null,"LastProcessedRunId":2,"LastProcessedAtUtc":"2026-11-15T01:05:00+00:00","TimeNeedsReview":false}','{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}}',NULL,NULL,'{"Uptime":null,"RebootedAtUtc":null,"RawSourceValues":null}');
INSERT INTO "ap_current" VALUES('145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','8a89d515-8e65-45d0-8f4f-1d7193f51898',2,2,'{"Identity":{"InstanceId":"__INSTANCE__","DatasetId":"__DATASET__","SourceApId":"200","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","Identity":{"SerialNumber":"SERIAL-200","WiredMac":null},"Fields":{"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}}},"MonitoringIntervalId":"8a89d515-8e65-45d0-8f4f-1d7193f51898","MonitoringStatus":0,"LastValidState":0,"LastValidRunId":2,"LastValidObservedAtUtc":"2026-11-15T01:05:00+00:00","LastUpAtUtc":"2026-11-15T01:05:00+00:00","OpenIncident":null,"OpenGap":null,"LastProcessedRunId":2,"LastProcessedAtUtc":"2026-11-15T01:05:00+00:00","TimeNeedsReview":false}','{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}}',NULL,NULL,'{"Uptime":null,"RebootedAtUtc":null,"RawSourceValues":null}');
CREATE TABLE aps (
    ap_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    instance_id TEXT NOT NULL,
    source_ap_id TEXT NOT NULL,
    UNIQUE(dataset_id,instance_id,source_ap_id)
);
INSERT INTO "aps" VALUES('64e6c1b7-0428-497e-bcef-b5434a7656e7','__DATASET__','__INSTANCE__','100');
INSERT INTO "aps" VALUES('145e71c4-711e-4d12-899c-9f2e56f77c22','__DATASET__','__INSTANCE__','200');
CREATE TABLE authentication_state (
    dataset_id TEXT NOT NULL,
    normalized_origin TEXT NOT NULL,
    credential_version INTEGER NOT NULL CHECK (credential_version > 0),
    blocked_reason TEXT NULL CHECK (blocked_reason IN ('LoginRejected', 'RepeatedSessionExpiry')),
    blocked_at_utc TEXT NULL,
    credential_registration_pending INTEGER NOT NULL DEFAULT 0 CHECK (credential_registration_pending IN (0, 1)),
    PRIMARY KEY (dataset_id, normalized_origin, credential_version),
    CHECK ((blocked_reason IS NULL) = (blocked_at_utc IS NULL))
);
CREATE TABLE collection_commits (
    commit_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL UNIQUE REFERENCES runs(run_id)
);
INSERT INTO "collection_commits" VALUES(1,1);
INSERT INTO "collection_commits" VALUES(2,2);
CREATE TABLE dataset (
    dataset_id TEXT PRIMARY KEY,
    mode INTEGER NOT NULL,
    schema_version INTEGER NOT NULL CHECK(schema_version > 0)
);
INSERT INTO "dataset" VALUES('__DATASET__',0,5);
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
CREATE TABLE enrichment_accepted_records (
    record_id INTEGER PRIMARY KEY REFERENCES enrichment_records(record_id),
    profile_key TEXT NOT NULL,
    record_type INTEGER NOT NULL,
    entity_key TEXT NOT NULL,
    run_id INTEGER NOT NULL REFERENCES runs(run_id)
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
CREATE TABLE enrichment_current (
    profile_key TEXT NOT NULL,
    record_type INTEGER NOT NULL,
    entity_key TEXT NOT NULL,
    record_id INTEGER NOT NULL REFERENCES enrichment_records(record_id),
    PRIMARY KEY(profile_key,record_type,entity_key)
);
CREATE TABLE enrichment_gap_versions (
    gap_id TEXT NOT NULL REFERENCES enrichment_gaps(gap_id),
    version INTEGER NOT NULL,
    recorded_at_utc TEXT NOT NULL,
    view_json TEXT NOT NULL,
    PRIMARY KEY(gap_id,version)
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
CREATE TABLE enrichment_power_current (
    profile_key TEXT NOT NULL,
    entity_key TEXT NOT NULL,
    sample_sequence INTEGER REFERENCES power_samples(sample_sequence) ON DELETE SET NULL,
    PRIMARY KEY(profile_key,entity_key)
);
CREATE TABLE enrichment_power_reads (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ordinal INTEGER NOT NULL,
    sample_sequence INTEGER REFERENCES power_samples(sample_sequence) ON DELETE SET NULL,
    read_at_utc TEXT NOT NULL,
    PRIMARY KEY(run_id,ordinal)
);
CREATE TABLE enrichment_record_targets (
    record_id INTEGER NOT NULL REFERENCES enrichment_records(record_id),
    ordinal INTEGER NOT NULL,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    point_id TEXT REFERENCES installation_points(point_id),
    PRIMARY KEY(record_id,ordinal)
);
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
CREATE TABLE enrichment_restore_events (
    restore_event_id TEXT NOT NULL REFERENCES restore_events(restore_event_id),
    kind INTEGER NOT NULL,
    consumed_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    PRIMARY KEY(restore_event_id,kind)
);
CREATE TABLE enrichment_retention_state (key TEXT PRIMARY KEY,cutoff_utc TEXT NOT NULL);
CREATE TABLE enrichment_run_bindings (
    run_id INTEGER PRIMARY KEY REFERENCES runs(run_id),
    profile_key TEXT NOT NULL,
    profile_json TEXT NOT NULL,
    restore_epoch INTEGER NOT NULL,
    connection_record_ids_json TEXT NOT NULL
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
INSERT INTO "enrichment_snapshot_refs" VALUES(1,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4','FB8DD1C6747F0DEA472D6F60CC861FFC584CC871A931897355B8169C663FD55B','E5480960D9202236979B7568DF53AFC74BBC0EB58877BCD8AAD3038C8904E7F1','{"Records":[],"Power":[],"Gaps":[]}');
INSERT INTO "enrichment_snapshot_refs" VALUES(2,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','8a89d515-8e65-45d0-8f4f-1d7193f51898','FB8DD1C6747F0DEA472D6F60CC861FFC584CC871A931897355B8169C663FD55B','E5480960D9202236979B7568DF53AFC74BBC0EB58877BCD8AAD3038C8904E7F1','{"Records":[],"Power":[],"Gaps":[]}');
CREATE TABLE enrichment_target_bindings (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    target_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE expected_targets (
    dataset_id TEXT NOT NULL,
    scope_version INTEGER NOT NULL,
    source_ap_id TEXT NOT NULL,
    PRIMARY KEY(dataset_id, scope_version, source_ap_id),
    FOREIGN KEY(dataset_id, scope_version) REFERENCES scopes(dataset_id, scope_version)
);
INSERT INTO "expected_targets" VALUES('__DATASET__',1,'100');
INSERT INTO "expected_targets" VALUES('__DATASET__',1,'200');
CREATE TABLE generations (
    generation_id TEXT PRIMARY KEY,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    started_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ended_run_id INTEGER REFERENCES runs(run_id),
    replacement_needs_confirmation INTEGER NOT NULL CHECK(replacement_needs_confirmation IN (0,1))
);
INSERT INTO "generations" VALUES('91bb66be-0dce-4988-acf1-3734e89b7805','64e6c1b7-0428-497e-bcef-b5434a7656e7',1,NULL,0);
INSERT INTO "generations" VALUES('94dc319c-72db-4f0c-a3d0-900dfc64d6d6','145e71c4-711e-4d12-899c-9f2e56f77c22',1,NULL,0);
CREATE TABLE import_previews (
    preview_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    preview_json TEXT NOT NULL,
    placement_versions_json TEXT NOT NULL,
    recorded_at_utc TEXT NOT NULL
);
CREATE TABLE incident_snapshots (
    incident_id TEXT NOT NULL REFERENCES incidents(incident_id),
    snapshot_kind INTEGER NOT NULL CHECK(snapshot_kind IN (0,1)),
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    snapshot_json TEXT NOT NULL,
    PRIMARY KEY(incident_id,snapshot_kind)
);
INSERT INTO "incident_snapshots" VALUES('f11ffda2-e456-4f24-8f4e-16bb8dde5fd1',0,1,'{"DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ScopeVersion":1,"RunId":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Device":{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","SourceApId":"100","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","Metadata":{"Identity":{"SerialNumber":"SERIAL-100","WiredMac":null},"Device":{"Name":"AP-100","IpAddress":"10.0.0.100","Model":null,"Firmware":null,"Uptime":null,"RebootedAtUtc":null},"Location":{"Site":null,"Building":null,"Floor":null,"Zone":null,"Detail":null,"InstallationPointId":null,"AirWaveRawLocation":null,"Folder":null,"Group":null},"Controller":{"SourceId":null,"Name":null,"IpAddress":null},"SourceManagement":{"MaintenanceMode":null,"ManagementStatus":null},"ManualManagement":null,"Upstream":{"SourceSwitchId":null,"PortIndex":null,"PortName":null,"ApInterface":null},"RawSourceValues":null,"ManualInventory":null},"Fields":{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2}},"SourceAtUtc":null,"LastContactedAtUtc":null,"Enrichment":[],"EnrichmentDetail":{"ThroughRunId":1,"ThroughCommitSequence":0,"CapturedAtUtc":"2026-11-15T01:00:00+00:00","Connections":[],"Ports":[],"PowerSamples":[],"PowerStates":[],"Gaps":[],"PowerSamplesExpired":false}},"SchemaVersion":1}');
INSERT INTO "incident_snapshots" VALUES('f11ffda2-e456-4f24-8f4e-16bb8dde5fd1',1,2,'{"DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ScopeVersion":1,"RunId":2,"RecordedAtUtc":"2026-11-15T01:05:00+00:00","Device":{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","SourceApId":"100","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","Metadata":{"Identity":{"SerialNumber":"SERIAL-100","WiredMac":null},"Device":{"Name":"Renamed","IpAddress":"10.0.0.100","Model":null,"Firmware":null,"Uptime":null,"RebootedAtUtc":null},"Location":{"Site":null,"Building":null,"Floor":null,"Zone":null,"Detail":null,"InstallationPointId":null,"AirWaveRawLocation":null,"Folder":null,"Group":null},"Controller":{"SourceId":null,"Name":null,"IpAddress":null},"SourceManagement":{"MaintenanceMode":null,"ManagementStatus":null},"ManualManagement":null,"Upstream":{"SourceSwitchId":null,"PortIndex":null,"PortName":null,"ApInterface":null},"RawSourceValues":null,"ManualInventory":null},"Fields":{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}},"SourceAtUtc":null,"LastContactedAtUtc":null,"Enrichment":[],"EnrichmentDetail":{"ThroughRunId":2,"ThroughCommitSequence":1,"CapturedAtUtc":"2026-11-15T01:05:00+00:00","Connections":[],"Ports":[],"PowerSamples":[],"PowerStates":[],"Gaps":[],"PowerSamplesExpired":false}},"SchemaVersion":1}');
CREATE TABLE incidents (
    incident_id TEXT PRIMARY KEY,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    is_open INTEGER NOT NULL CHECK(is_open IN (0,1)),
    tracking_json TEXT NOT NULL
);
INSERT INTO "incidents" VALUES('f11ffda2-e456-4f24-8f4e-16bb8dde5fd1','64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4',0,'{"IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","FirstDownRunId":1,"FirstDownAtUtc":"2026-11-15T01:00:00+00:00","LastDownRunId":1,"LastDownAtUtc":"2026-11-15T01:00:00+00:00","LastUpAtUtc":null,"IsInitialDown":true,"HasObservationGap":false,"RecoveredRunId":2,"RecoveredAtUtc":"2026-11-15T01:05:00+00:00","TrackingEndedRunId":null,"TrackingEndedAtUtc":null,"TrackingEndReason":null,"TimeNeedsReview":false,"ActualStartUnknown":true,"ActualRecoveryUnknown":true,"ConfirmedElapsed":"00:05:00"}');
CREATE TABLE installation_point_versions (
    point_id TEXT NOT NULL REFERENCES installation_points(point_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    PRIMARY KEY(point_id,version)
);
CREATE TABLE installation_points (
    point_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    inventory_key TEXT,
    current_version INTEGER NOT NULL,
    UNIQUE(dataset_id,inventory_key)
);
CREATE TABLE manual_inventory_current (
    generation_id TEXT PRIMARY KEY REFERENCES generations(generation_id),
    version INTEGER NOT NULL,
    FOREIGN KEY(generation_id,version) REFERENCES manual_inventory_versions(generation_id,version)
);
CREATE TABLE manual_inventory_versions (
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    version INTEGER NOT NULL,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    version_json TEXT NOT NULL,
    PRIMARY KEY(generation_id,version)
);
CREATE TABLE metadata_changes (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    before_version_id INTEGER REFERENCES metadata_versions(metadata_version_id),
    after_version_id INTEGER NOT NULL REFERENCES metadata_versions(metadata_version_id),
    PRIMARY KEY(run_id,ap_id)
);
INSERT INTO "metadata_changes" VALUES(1,'64e6c1b7-0428-497e-bcef-b5434a7656e7',NULL,1);
INSERT INTO "metadata_changes" VALUES(1,'145e71c4-711e-4d12-899c-9f2e56f77c22',NULL,2);
INSERT INTO "metadata_changes" VALUES(2,'64e6c1b7-0428-497e-bcef-b5434a7656e7',1,3);
CREATE TABLE metadata_versions (
    metadata_version_id INTEGER PRIMARY KEY AUTOINCREMENT,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    metadata_json TEXT NOT NULL,
    UNIQUE(ap_id,generation_id,metadata_json)
);
INSERT INTO "metadata_versions" VALUES(1,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','{"Identity":{"SerialNumber":"SERIAL-100","WiredMac":null},"Device":{"Name":"AP-100","IpAddress":"10.0.0.100","Model":null,"Firmware":null,"Uptime":null,"RebootedAtUtc":null},"Location":{"Site":null,"Building":null,"Floor":null,"Zone":null,"Detail":null,"InstallationPointId":null,"AirWaveRawLocation":null,"Folder":null,"Group":null},"Controller":{"SourceId":null,"Name":null,"IpAddress":null},"SourceManagement":{"MaintenanceMode":null,"ManagementStatus":null},"ManualManagement":null,"Upstream":{"SourceSwitchId":null,"PortIndex":null,"PortName":null,"ApInterface":null},"RawSourceValues":null,"ManualInventory":null}');
INSERT INTO "metadata_versions" VALUES(2,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','{"Identity":{"SerialNumber":"SERIAL-200","WiredMac":null},"Device":{"Name":"AP-200","IpAddress":"10.0.0.200","Model":null,"Firmware":null,"Uptime":null,"RebootedAtUtc":null},"Location":{"Site":null,"Building":null,"Floor":null,"Zone":null,"Detail":null,"InstallationPointId":null,"AirWaveRawLocation":null,"Folder":null,"Group":null},"Controller":{"SourceId":null,"Name":null,"IpAddress":null},"SourceManagement":{"MaintenanceMode":null,"ManagementStatus":null},"ManualManagement":null,"Upstream":{"SourceSwitchId":null,"PortIndex":null,"PortName":null,"ApInterface":null},"RawSourceValues":null,"ManualInventory":null}');
INSERT INTO "metadata_versions" VALUES(3,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','{"Identity":{"SerialNumber":"SERIAL-100","WiredMac":null},"Device":{"Name":"Renamed","IpAddress":"10.0.0.100","Model":null,"Firmware":null,"Uptime":null,"RebootedAtUtc":null},"Location":{"Site":null,"Building":null,"Floor":null,"Zone":null,"Detail":null,"InstallationPointId":null,"AirWaveRawLocation":null,"Folder":null,"Group":null},"Controller":{"SourceId":null,"Name":null,"IpAddress":null},"SourceManagement":{"MaintenanceMode":null,"ManagementStatus":null},"ManualManagement":null,"Upstream":{"SourceSwitchId":null,"PortIndex":null,"PortName":null,"ApInterface":null},"RawSourceValues":null,"ManualInventory":null}');
CREATE TABLE monitoring_changes (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    status INTEGER NOT NULL,
    reason TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
CREATE TABLE monitoring_intervals (
    interval_id TEXT PRIMARY KEY,
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    started_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ended_run_id INTEGER REFERENCES runs(run_id),
    end_reason TEXT
);
INSERT INTO "monitoring_intervals" VALUES('c393867f-731a-4f59-b94d-8f43ec009df4','91bb66be-0dce-4988-acf1-3734e89b7805',1,NULL,NULL);
INSERT INTO "monitoring_intervals" VALUES('8a89d515-8e65-45d0-8f4f-1d7193f51898','94dc319c-72db-4f0c-a3d0-900dfc64d6d6',1,NULL,NULL);
CREATE TABLE observation_gaps (
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    first_run_id INTEGER NOT NULL REFERENCES runs(run_id),
    gap_json TEXT NOT NULL,
    PRIMARY KEY(ap_id,generation_id,interval_id,first_run_id)
);
CREATE TABLE operation_commands (
    command_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    kind TEXT NOT NULL,
    payload_hash TEXT NOT NULL,
    result_json TEXT NOT NULL,
    recorded_at_utc TEXT NOT NULL
);
CREATE TABLE operation_event_targets (
    event_id INTEGER NOT NULL REFERENCES operation_events(event_id),
    ordinal INTEGER NOT NULL,
    ap_id TEXT REFERENCES aps(ap_id),
    generation_id TEXT REFERENCES generations(generation_id),
    incident_id TEXT REFERENCES incidents(incident_id),
    point_id TEXT REFERENCES installation_points(point_id),
    PRIMARY KEY(event_id,ordinal)
);
INSERT INTO "operation_event_targets" VALUES(1,0,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(2,0,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(3,0,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(4,0,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','f11ffda2-e456-4f24-8f4e-16bb8dde5fd1',NULL);
INSERT INTO "operation_event_targets" VALUES(5,0,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(6,0,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(7,0,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(8,0,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805',NULL,NULL);
INSERT INTO "operation_event_targets" VALUES(9,0,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','f11ffda2-e456-4f24-8f4e-16bb8dde5fd1',NULL);
CREATE TABLE operation_events (
    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    kind INTEGER NOT NULL,
    recorded_at_utc TEXT NOT NULL,
    origin_key TEXT NOT NULL UNIQUE,
    run_id INTEGER REFERENCES runs(run_id),
    event_json TEXT NOT NULL
);
INSERT INTO "operation_events" VALUES(1,'__DATASET__',1,'2026-11-15T01:00:00.0000000+00:00','run:1:ap:64e6c1b7-0428-497e-bcef-b5434a7656e7:change:BaselineRegistered:serial_number',1,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"BaselineRegistered","Field":"serial_number","BeforeValue":null,"AfterValue":"SERIAL-100","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","IncidentId":null,"InstallationPointId":null}],"RunId":1,"AutomaticChange":{"Kind":1,"Semantics":0,"Field":"serial_number","BeforeValue":null,"AfterValue":"SERIAL-100","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","PreviousDeviceGenerationId":null,"RunId":1,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:00:00+00:00","BeforeEvidence":null,"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":false},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(2,'__DATASET__',1,'2026-11-15T01:00:00.0000000+00:00','run:1:ap:64e6c1b7-0428-497e-bcef-b5434a7656e7:change:BaselineRegistered:name',1,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"BaselineRegistered","Field":"name","BeforeValue":null,"AfterValue":"AP-100","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","IncidentId":null,"InstallationPointId":null}],"RunId":1,"AutomaticChange":{"Kind":1,"Semantics":0,"Field":"name","BeforeValue":null,"AfterValue":"AP-100","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","PreviousDeviceGenerationId":null,"RunId":1,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:00:00+00:00","BeforeEvidence":null,"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":false},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(3,'__DATASET__',1,'2026-11-15T01:00:00.0000000+00:00','run:1:ap:64e6c1b7-0428-497e-bcef-b5434a7656e7:change:BaselineRegistered:lan_ip',1,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"BaselineRegistered","Field":"lan_ip","BeforeValue":null,"AfterValue":"10.0.0.100","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","IncidentId":null,"InstallationPointId":null}],"RunId":1,"AutomaticChange":{"Kind":1,"Semantics":0,"Field":"lan_ip","BeforeValue":null,"AfterValue":"10.0.0.100","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","PreviousDeviceGenerationId":null,"RunId":1,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:00:00+00:00","BeforeEvidence":null,"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":false},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(4,'__DATASET__',0,'2026-11-15T01:00:00.0000000+00:00','run:1:incident:f11ffda2-e456-4f24-8f4e-16bb8dde5fd1:Created',1,'{"EventId":0,"Kind":0,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"Created","Field":"incident","BeforeValue":null,"AfterValue":"Open","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","InstallationPointId":null}],"RunId":1,"AutomaticChange":null,"Work":null,"Placement":null,"Point":null,"Incident":{"IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","FirstDownRunId":1,"FirstDownAtUtc":"2026-11-15T01:00:00+00:00","LastDownRunId":1,"LastDownAtUtc":"2026-11-15T01:00:00+00:00","LastUpAtUtc":null,"IsInitialDown":true,"HasObservationGap":false,"RecoveredRunId":null,"RecoveredAtUtc":null,"TrackingEndedRunId":null,"TrackingEndedAtUtc":null,"TrackingEndReason":null,"TimeNeedsReview":false,"ActualStartUnknown":true,"ActualRecoveryUnknown":false,"ConfirmedElapsed":"00:00:00"},"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(5,'__DATASET__',1,'2026-11-15T01:00:00.0000000+00:00','run:1:ap:145e71c4-711e-4d12-899c-9f2e56f77c22:change:BaselineRegistered:serial_number',1,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"BaselineRegistered","Field":"serial_number","BeforeValue":null,"AfterValue":"SERIAL-200","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"145e71c4-711e-4d12-899c-9f2e56f77c22","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","IncidentId":null,"InstallationPointId":null}],"RunId":1,"AutomaticChange":{"Kind":1,"Semantics":0,"Field":"serial_number","BeforeValue":null,"AfterValue":"SERIAL-200","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"145e71c4-711e-4d12-899c-9f2e56f77c22","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","PreviousDeviceGenerationId":null,"RunId":1,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:00:00+00:00","BeforeEvidence":null,"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":false},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(6,'__DATASET__',1,'2026-11-15T01:00:00.0000000+00:00','run:1:ap:145e71c4-711e-4d12-899c-9f2e56f77c22:change:BaselineRegistered:name',1,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"BaselineRegistered","Field":"name","BeforeValue":null,"AfterValue":"AP-200","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"145e71c4-711e-4d12-899c-9f2e56f77c22","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","IncidentId":null,"InstallationPointId":null}],"RunId":1,"AutomaticChange":{"Kind":1,"Semantics":0,"Field":"name","BeforeValue":null,"AfterValue":"AP-200","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"145e71c4-711e-4d12-899c-9f2e56f77c22","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","PreviousDeviceGenerationId":null,"RunId":1,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:00:00+00:00","BeforeEvidence":null,"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":false},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(7,'__DATASET__',1,'2026-11-15T01:00:00.0000000+00:00','run:1:ap:145e71c4-711e-4d12-899c-9f2e56f77c22:change:BaselineRegistered:lan_ip',1,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:00:00+00:00","Source":1,"Action":"BaselineRegistered","Field":"lan_ip","BeforeValue":null,"AfterValue":"10.0.0.200","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"145e71c4-711e-4d12-899c-9f2e56f77c22","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","IncidentId":null,"InstallationPointId":null}],"RunId":1,"AutomaticChange":{"Kind":1,"Semantics":0,"Field":"lan_ip","BeforeValue":null,"AfterValue":"10.0.0.200","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"145e71c4-711e-4d12-899c-9f2e56f77c22","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","PreviousDeviceGenerationId":null,"RunId":1,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:00:00+00:00","BeforeEvidence":null,"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":false},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(8,'__DATASET__',1,'2026-11-15T01:05:00.0000000+00:00','run:2:ap:64e6c1b7-0428-497e-bcef-b5434a7656e7:change:ValueChanged:name',2,'{"EventId":0,"Kind":1,"RecordedAtUtc":"2026-11-15T01:05:00+00:00","Source":1,"Action":"ValueChanged","Field":"name","BeforeValue":"AP-100","AfterValue":"Renamed","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","IncidentId":null,"InstallationPointId":null}],"RunId":2,"AutomaticChange":{"Kind":1,"Semantics":1,"Field":"name","BeforeValue":"AP-100","AfterValue":"Renamed","DatasetId":"__DATASET__","InstanceId":"__INSTANCE__","ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","PreviousDeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","RunId":2,"ScopeVersion":1,"ObservedAtUtc":"2026-11-15T01:05:00+00:00","BeforeEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"AfterEvidence":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"HasObservationGap":false,"TimeNeedsReview":false,"NeedsConfirmation":false,"IsChangedValue":true},"Work":null,"Placement":null,"Point":null,"Incident":null,"TimeNeedsReview":false,"ReplacementConfirmation":null}');
INSERT INTO "operation_events" VALUES(9,'__DATASET__',0,'2026-11-15T01:05:00.0000000+00:00','run:2:incident:f11ffda2-e456-4f24-8f4e-16bb8dde5fd1:Recovered',2,'{"EventId":0,"Kind":0,"RecordedAtUtc":"2026-11-15T01:05:00+00:00","Source":1,"Action":"Recovered","Field":"incident","BeforeValue":"Open","AfterValue":"Recovered","Reason":null,"ActualWorkAtUtc":null,"Targets":[{"ApId":"64e6c1b7-0428-497e-bcef-b5434a7656e7","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","InstallationPointId":null}],"RunId":2,"AutomaticChange":null,"Work":null,"Placement":null,"Point":null,"Incident":{"IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","FirstDownRunId":1,"FirstDownAtUtc":"2026-11-15T01:00:00+00:00","LastDownRunId":1,"LastDownAtUtc":"2026-11-15T01:00:00+00:00","LastUpAtUtc":null,"IsInitialDown":true,"HasObservationGap":false,"RecoveredRunId":2,"RecoveredAtUtc":"2026-11-15T01:05:00+00:00","TrackingEndedRunId":null,"TrackingEndedAtUtc":null,"TrackingEndReason":null,"TimeNeedsReview":false,"ActualStartUnknown":true,"ActualRecoveryUnknown":true,"ConfirmedElapsed":"00:05:00"},"TimeNeedsReview":false,"ReplacementConfirmation":null}');
CREATE TABLE operational_logs (
    log_id INTEGER PRIMARY KEY AUTOINCREMENT,
    recorded_at_utc TEXT NOT NULL,
    category TEXT NOT NULL CHECK(category IN ('BackupSucceeded','BackupFailed','RetentionSucceeded','RetentionFailed','RestoreApplied')),
    related_run_id INTEGER REFERENCES runs(run_id)
);
CREATE TABLE operations_applied_bindings (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    binding_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
INSERT INTO "operations_applied_bindings" VALUES(1,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','{"GenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","Inventory":null,"Policy":null,"Placement":null,"PlacementVersion":0,"PointVersion":null,"PolicySource":null,"LegacyManagement":null}');
INSERT INTO "operations_applied_bindings" VALUES(1,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','{"GenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","Inventory":null,"Policy":null,"Placement":null,"PlacementVersion":0,"PointVersion":null,"PolicySource":null,"LegacyManagement":null}');
INSERT INTO "operations_applied_bindings" VALUES(2,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','{"GenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","Inventory":null,"Policy":null,"Placement":null,"PlacementVersion":0,"PointVersion":null,"PolicySource":null,"LegacyManagement":null}');
INSERT INTO "operations_applied_bindings" VALUES(2,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','{"GenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","Inventory":null,"Policy":null,"Placement":null,"PlacementVersion":0,"PointVersion":null,"PolicySource":null,"LegacyManagement":null}');
CREATE TABLE operations_restore_epochs (
    epoch INTEGER PRIMARY KEY AUTOINCREMENT,
    restore_event_id TEXT NOT NULL UNIQUE REFERENCES restore_events(restore_event_id)
);
CREATE TABLE operations_state (key TEXT PRIMARY KEY,value INTEGER NOT NULL);
INSERT INTO "operations_state" VALUES('legacy_backfill',1);
CREATE TABLE placement_current (
    generation_id TEXT PRIMARY KEY REFERENCES generations(generation_id),
    version INTEGER NOT NULL,
    placement_id TEXT REFERENCES placements(placement_id)
);
CREATE TABLE placement_versions (
    placement_id TEXT NOT NULL REFERENCES placements(placement_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    PRIMARY KEY(placement_id,version)
);
CREATE TABLE placements (
    placement_id TEXT PRIMARY KEY,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    point_id TEXT NOT NULL REFERENCES installation_points(point_id),
    current_version INTEGER NOT NULL
);
CREATE TABLE power_sample_keys (
    key_hash TEXT PRIMARY KEY,
    measurement_hash TEXT NOT NULL,
    sample_sequence INTEGER REFERENCES power_samples(sample_sequence) ON DELETE SET NULL
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
CREATE TABLE profiles (
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    configuration_version INTEGER NOT NULL CHECK(configuration_version > 0),
    profile_json TEXT NOT NULL,
    PRIMARY KEY(dataset_id, configuration_version)
);
INSERT INTO "profiles" VALUES('__DATASET__',1,'{"SchemaVersion":1,"InstanceId":"__INSTANCE__","DatasetId":"__DATASET__","Origin":"https://airwave.example.invalid/","ConfigurationVersion":1,"ScopeVersion":1,"ExpectedTargets":[{"SourceApId":"100","DeviceGenerationId":null,"MonitoringIntervalId":null},{"SourceApId":"200","DeviceGenerationId":null,"MonitoringIntervalId":null}],"BaselineStatus":0,"Settings":{"ApIntervalMinutes":5,"EnrichmentIntervalMinutes":15,"ConnectionCollectionEnabled":false,"PowerCollectionEnabled":false,"RequestTimeout":"00:00:45","RunTimeout":"00:02:00","MaximumDecompressedResponseBytes":67108864},"Proxy":{"Mode":0,"Address":null},"CredentialsVersion":1}');
CREATE TABLE replacement_confirmations (
    replacement_event_id INTEGER PRIMARY KEY REFERENCES operation_events(event_id),
    confirmation_event_id INTEGER NOT NULL UNIQUE REFERENCES operation_events(event_id),
    work_id TEXT NOT NULL,
    work_version INTEGER NOT NULL,
    FOREIGN KEY(work_id,work_version) REFERENCES work_versions(work_id,version)
);
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
INSERT INTO "run_execution_details" VALUES(1,'2026-11-15T01:00:00.0000000+00:00',0,2,2,0,'[]');
INSERT INTO "run_execution_details" VALUES(2,'2026-11-15T01:05:00.0000000+00:00',0,2,2,0,'[]');
CREATE TABLE run_manual_bindings (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    binding_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
INSERT INTO "run_manual_bindings" VALUES(2,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','{"GenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","Inventory":null,"Policy":null,"Placement":null,"PlacementVersion":0,"PointVersion":null,"PolicySource":null,"LegacyManagement":null}');
INSERT INTO "run_manual_bindings" VALUES(2,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','{"GenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","Inventory":null,"Policy":null,"Placement":null,"PlacementVersion":0,"PointVersion":null,"PolicySource":null,"LegacyManagement":null}');
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
INSERT INTO "run_results" VALUES(1,'__DATASET__',0,'2026-11-15T01:00:00.0000000+00:00',0,'{"RunId":1,"AlreadyApplied":false,"Outcome":0,"Summary":{"ConfirmedUp":1,"ConfirmedDown":1,"Unknown":0,"NewDown":0,"InitialDown":1,"ExistingDown":0,"Recovered":0,"OpenIncidents":1,"TargetCount":2,"ExcludedCount":0,"BaselineStatus":0},"SchemaVersion":1}',0,'[]','{"ParsedApCount":2,"ValidStateCount":2,"AppliedStateCount":2,"ExcludedRecords":[],"IdentityConflicts":{"DuplicateApIds":[],"SerialCollisions":{}}}');
INSERT INTO "run_results" VALUES(2,'__DATASET__',0,'2026-11-15T01:05:00.0000000+00:00',0,'{"RunId":2,"AlreadyApplied":false,"Outcome":0,"Summary":{"ConfirmedUp":2,"ConfirmedDown":0,"Unknown":0,"NewDown":0,"InitialDown":0,"ExistingDown":0,"Recovered":1,"OpenIncidents":0,"TargetCount":2,"ExcludedCount":0,"BaselineStatus":0},"SchemaVersion":1}',0,'[]','{"ParsedApCount":2,"ValidStateCount":2,"AppliedStateCount":2,"ExcludedRecords":[],"IdentityConflicts":{"DuplicateApIds":[],"SerialCollisions":{}}}');
CREATE TABLE run_rows (
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    metadata_version_id INTEGER NOT NULL REFERENCES metadata_versions(metadata_version_id),
    facts_json TEXT NOT NULL,
    PRIMARY KEY(run_id,ap_id)
);
INSERT INTO "run_rows" VALUES(1,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4',1,'{"CurrentState":1,"LastValidState":1,"LastValidRunId":1,"LastValidObservedAtUtc":"2026-11-15T01:00:00+00:00","FirstDownAtUtc":"2026-11-15T01:00:00+00:00","OpenIncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","Classification":2,"Fields":{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2}},"UnknownReason":null,"TimeNeedsReview":false,"SourceAtUtc":null,"LastContactedAtUtc":null,"Measurements":{"Uptime":null,"RebootedAtUtc":null,"RawSourceValues":null},"EnrichmentReferenceId":1,"EnrichmentCommitSequence":0,"EnrichmentCapturedAtUtc":"2026-11-15T01:00:00+00:00"}');
INSERT INTO "run_rows" VALUES(1,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','8a89d515-8e65-45d0-8f4f-1d7193f51898',2,'{"CurrentState":0,"LastValidState":0,"LastValidRunId":1,"LastValidObservedAtUtc":"2026-11-15T01:00:00+00:00","FirstDownAtUtc":null,"OpenIncidentId":null,"Classification":0,"Fields":{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:00:00+00:00","SourceAtUtc":null,"Freshness":2}},"UnknownReason":null,"TimeNeedsReview":false,"SourceAtUtc":null,"LastContactedAtUtc":null,"Measurements":{"Uptime":null,"RebootedAtUtc":null,"RawSourceValues":null},"EnrichmentReferenceId":2,"EnrichmentCommitSequence":0,"EnrichmentCapturedAtUtc":"2026-11-15T01:00:00+00:00"}');
INSERT INTO "run_rows" VALUES(2,'64e6c1b7-0428-497e-bcef-b5434a7656e7','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4',3,'{"CurrentState":0,"LastValidState":0,"LastValidRunId":2,"LastValidObservedAtUtc":"2026-11-15T01:05:00+00:00","FirstDownAtUtc":"2026-11-15T01:00:00+00:00","OpenIncidentId":null,"Classification":4,"Fields":{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}},"UnknownReason":null,"TimeNeedsReview":false,"SourceAtUtc":null,"LastContactedAtUtc":null,"Measurements":{"Uptime":null,"RebootedAtUtc":null,"RawSourceValues":null},"EnrichmentReferenceId":1,"EnrichmentCommitSequence":1,"EnrichmentCapturedAtUtc":"2026-11-15T01:05:00+00:00"}');
INSERT INTO "run_rows" VALUES(2,'145e71c4-711e-4d12-899c-9f2e56f77c22','94dc319c-72db-4f0c-a3d0-900dfc64d6d6','8a89d515-8e65-45d0-8f4f-1d7193f51898',2,'{"CurrentState":0,"LastValidState":0,"LastValidRunId":2,"LastValidObservedAtUtc":"2026-11-15T01:05:00+00:00","FirstDownAtUtc":null,"OpenIncidentId":null,"Classification":0,"Fields":{"name":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"serial_number":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"lan_ip":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2},"is_up":{"Availability":0,"Validity":0,"Source":1,"CollectedAtUtc":"2026-11-15T01:05:00+00:00","SourceAtUtc":null,"Freshness":2}},"UnknownReason":null,"TimeNeedsReview":false,"SourceAtUtc":null,"LastContactedAtUtc":null,"Measurements":{"Uptime":null,"RebootedAtUtc":null,"RawSourceValues":null},"EnrichmentReferenceId":2,"EnrichmentCommitSequence":1,"EnrichmentCapturedAtUtc":"2026-11-15T01:05:00+00:00"}');
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
INSERT INTO "run_transitions" VALUES(1,'64e6c1b7-0428-497e-bcef-b5434a7656e7','f11ffda2-e456-4f24-8f4e-16bb8dde5fd1','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4',0,'{"IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","FirstDownRunId":1,"FirstDownAtUtc":"2026-11-15T01:00:00+00:00","LastDownRunId":1,"LastDownAtUtc":"2026-11-15T01:00:00+00:00","LastUpAtUtc":null,"IsInitialDown":true,"HasObservationGap":false,"RecoveredRunId":null,"RecoveredAtUtc":null,"TrackingEndedRunId":null,"TrackingEndedAtUtc":null,"TrackingEndReason":null,"TimeNeedsReview":false,"ActualStartUnknown":true,"ActualRecoveryUnknown":false,"ConfirmedElapsed":"00:00:00"}');
INSERT INTO "run_transitions" VALUES(2,'64e6c1b7-0428-497e-bcef-b5434a7656e7','f11ffda2-e456-4f24-8f4e-16bb8dde5fd1','91bb66be-0dce-4988-acf1-3734e89b7805','c393867f-731a-4f59-b94d-8f43ec009df4',2,'{"IncidentId":"f11ffda2-e456-4f24-8f4e-16bb8dde5fd1","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4","FirstDownRunId":1,"FirstDownAtUtc":"2026-11-15T01:00:00+00:00","LastDownRunId":1,"LastDownAtUtc":"2026-11-15T01:00:00+00:00","LastUpAtUtc":null,"IsInitialDown":true,"HasObservationGap":false,"RecoveredRunId":2,"RecoveredAtUtc":"2026-11-15T01:05:00+00:00","TrackingEndedRunId":null,"TrackingEndedAtUtc":null,"TrackingEndReason":null,"TimeNeedsReview":false,"ActualStartUnknown":true,"ActualRecoveryUnknown":true,"ConfirmedElapsed":"00:05:00"}');
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
INSERT INTO "runs" VALUES(1,'__DATASET__',1,0,1,'{"RunId":1,"Profile":{"SchemaVersion":1,"InstanceId":"__INSTANCE__","DatasetId":"__DATASET__","Origin":"https://airwave.example.invalid/","ConfigurationVersion":1,"ScopeVersion":1,"ExpectedTargets":[{"SourceApId":"100","DeviceGenerationId":null,"MonitoringIntervalId":null},{"SourceApId":"200","DeviceGenerationId":null,"MonitoringIntervalId":null}],"BaselineStatus":0,"Settings":{"ApIntervalMinutes":5,"EnrichmentIntervalMinutes":15,"ConnectionCollectionEnabled":false,"PowerCollectionEnabled":false,"RequestTimeout":"00:00:45","RunTimeout":"00:02:00","MaximumDecompressedResponseBytes":67108864},"Proxy":{"Mode":0,"Address":null},"CredentialsVersion":1},"Kind":0,"Trigger":1,"StartedAtUtc":"2026-11-15T01:00:00+00:00","DeadlineUtc":"2026-11-15T01:02:00+00:00","SchemaVersion":1}',1);
INSERT INTO "runs" VALUES(2,'__DATASET__',1,0,1,'{"RunId":2,"Profile":{"SchemaVersion":1,"InstanceId":"__INSTANCE__","DatasetId":"__DATASET__","Origin":"https://airwave.example.invalid/","ConfigurationVersion":1,"ScopeVersion":1,"ExpectedTargets":[{"SourceApId":"100","DeviceGenerationId":"91bb66be-0dce-4988-acf1-3734e89b7805","MonitoringIntervalId":"c393867f-731a-4f59-b94d-8f43ec009df4"},{"SourceApId":"200","DeviceGenerationId":"94dc319c-72db-4f0c-a3d0-900dfc64d6d6","MonitoringIntervalId":"8a89d515-8e65-45d0-8f4f-1d7193f51898"}],"BaselineStatus":0,"Settings":{"ApIntervalMinutes":5,"EnrichmentIntervalMinutes":15,"ConnectionCollectionEnabled":false,"PowerCollectionEnabled":false,"RequestTimeout":"00:00:45","RunTimeout":"00:02:00","MaximumDecompressedResponseBytes":67108864},"Proxy":{"Mode":0,"Address":null},"CredentialsVersion":1},"Kind":0,"Trigger":1,"StartedAtUtc":"2026-11-15T01:05:00+00:00","DeadlineUtc":"2026-11-15T01:07:00+00:00","SchemaVersion":1}',1);
CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY CHECK(version > 0),
    applied_at_utc TEXT NOT NULL
);
INSERT INTO "schema_migrations" VALUES(1,'2026-11-15T01:00:00.0000000+00:00');
INSERT INTO "schema_migrations" VALUES(2,'2026-11-15T01:00:00.0000000+00:00');
INSERT INTO "schema_migrations" VALUES(3,'2026-11-15T01:00:00.0000000+00:00');
INSERT INTO "schema_migrations" VALUES(4,'2026-11-15T01:00:00.0000000+00:00');
INSERT INTO "schema_migrations" VALUES(5,'2026-11-15T01:00:00.0000000+00:00');
CREATE TABLE scopes (
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    scope_version INTEGER NOT NULL CHECK(scope_version > 0),
    instance_id TEXT NOT NULL,
    origin TEXT NOT NULL,
    baseline_status INTEGER NOT NULL,
    target_universe_known INTEGER NOT NULL CHECK(target_universe_known IN (0,1)),
    PRIMARY KEY(dataset_id, scope_version)
);
INSERT INTO "scopes" VALUES('__DATASET__',1,'__INSTANCE__','https://airwave.example.invalid/',0,1);
CREATE TABLE source_checkpoints (
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    source_name TEXT NOT NULL,
    position INTEGER NOT NULL CHECK(position >= 0),
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    PRIMARY KEY(dataset_id, source_name)
);
DELETE FROM "sqlite_sequence";
INSERT INTO "sqlite_sequence" VALUES('collection_commits',2);
INSERT INTO "sqlite_sequence" VALUES('operations_restore_epochs',0);
INSERT INTO "sqlite_sequence" VALUES('runs',2);
INSERT INTO "sqlite_sequence" VALUES('enrichment_snapshot_refs',4);
INSERT INTO "sqlite_sequence" VALUES('operation_events',9);
INSERT INTO "sqlite_sequence" VALUES('metadata_versions',4);
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
INSERT INTO "success_times" VALUES('__DATASET__',1,0,2,'2026-11-15T01:05:00.0000000+00:00',2,'2026-11-15T01:05:00.0000000+00:00');
CREATE TABLE tracking_policy_current (
    ap_id TEXT PRIMARY KEY REFERENCES aps(ap_id),
    version INTEGER NOT NULL,
    FOREIGN KEY(ap_id,version) REFERENCES tracking_policy_versions(ap_id,version)
);
CREATE TABLE tracking_policy_versions (
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    source INTEGER NOT NULL,
    PRIMARY KEY(ap_id,version)
);
CREATE TABLE work_items (
    work_id TEXT PRIMARY KEY,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    current_version INTEGER NOT NULL
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
CREATE TABLE work_versions (
    work_id TEXT NOT NULL REFERENCES work_items(work_id),
    version INTEGER NOT NULL,
    version_json TEXT NOT NULL,
    PRIMARY KEY(work_id,version)
);
CREATE UNIQUE INDEX one_running_ap_run ON runs(dataset_id) WHERE kind=0 AND lifecycle=0;
CREATE UNIQUE INDEX one_open_incident_per_generation ON incidents(generation_id) WHERE is_open=1;
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
CREATE INDEX execution_detail_expiry ON run_execution_details(recorded_at_utc);
CREATE INDEX operational_log_expiry ON operational_logs(recorded_at_utc);
CREATE INDEX placements_point ON placements(point_id,generation_id);
CREATE INDEX operation_event_filter ON operation_events(dataset_id,kind,recorded_at_utc,event_id);
CREATE INDEX operation_target_ap ON operation_event_targets(ap_id,generation_id,event_id);
CREATE INDEX operation_target_point ON operation_event_targets(point_id,event_id);
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
CREATE UNIQUE INDEX one_running_enrichment_kind ON runs(dataset_id,kind) WHERE kind IN (1,2) AND lifecycle=0;
CREATE INDEX enrichment_binding_profile ON enrichment_run_bindings(profile_key,run_id);
CREATE INDEX enrichment_connection_endpoint ON enrichment_connections(profile_key,local_key,remote_key,run_id);
CREATE INDEX enrichment_record_run ON enrichment_records(run_id,record_type,record_id);
CREATE INDEX enrichment_target_history ON enrichment_record_targets(ap_id,generation_id,interval_id,record_id);
CREATE INDEX power_profile_time ON power_samples(profile_key,read_at_utc,sample_sequence);
CREATE INDEX power_target_time ON power_samples(ap_id,generation_id,read_at_utc,sample_sequence);
CREATE INDEX power_endpoint_time ON power_samples(source_device_id,source_interface_id,metric,read_at_utc,sample_sequence);
CREATE UNIQUE INDEX one_open_enrichment_gap ON enrichment_gaps(profile_key,kind) WHERE is_open=1;
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
CREATE INDEX enrichment_accepted_record_selection ON enrichment_accepted_records(profile_key,record_type,entity_key,run_id,record_id);
CREATE INDEX enrichment_accepted_power_selection ON enrichment_accepted_power(profile_key,entity_key,run_id,accepted_sequence);
CREATE INDEX enrichment_accepted_power_target ON enrichment_accepted_power(ap_id,generation_id,interval_id,profile_key,entity_key);
CREATE TRIGGER enrichment_accepted_records_immutable BEFORE UPDATE ON enrichment_accepted_records BEGIN SELECT RAISE(ABORT,'Accepted evidence history is immutable'); END;
CREATE TRIGGER enrichment_accepted_records_no_delete BEFORE DELETE ON enrichment_accepted_records BEGIN SELECT RAISE(ABORT,'Accepted evidence history is permanent'); END;
CREATE TRIGGER enrichment_accepted_power_immutable BEFORE UPDATE ON enrichment_accepted_power BEGIN SELECT RAISE(ABORT,'Accepted power history is immutable'); END;
CREATE TRIGGER enrichment_accepted_power_no_delete BEFORE DELETE ON enrichment_accepted_power BEGIN SELECT RAISE(ABORT,'Accepted power history is permanent'); END;
COMMIT;
PRAGMA user_version=5;
