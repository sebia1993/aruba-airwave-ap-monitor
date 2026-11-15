-- Existing schema 1-5 rows remain byte-for-byte available. New normal observations share
-- immutable facts and membership; only their actual run context is recorded per run.
CREATE TABLE ap_history_versions (
    version_id INTEGER PRIMARY KEY AUTOINCREMENT,
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    generation_id TEXT NOT NULL REFERENCES generations(generation_id),
    interval_id TEXT NOT NULL REFERENCES monitoring_intervals(interval_id),
    metadata_version_id INTEGER NOT NULL REFERENCES metadata_versions(metadata_version_id),
    facts_hash TEXT NOT NULL,
    facts_template_json TEXT NOT NULL CHECK(json_extract(facts_template_json,'$.CurrentState') IS 0),
    UNIQUE(ap_id,generation_id,interval_id,metadata_version_id,facts_hash)
);
CREATE TABLE ap_history_sets (
    set_id INTEGER PRIMARY KEY AUTOINCREMENT,
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    scope_version INTEGER NOT NULL,
    members_hash TEXT NOT NULL,
    member_count INTEGER NOT NULL CHECK(member_count>=0),
    UNIQUE(dataset_id,scope_version,members_hash),
    FOREIGN KEY(dataset_id,scope_version) REFERENCES scopes(dataset_id,scope_version)
);
CREATE TABLE ap_history_members (
    set_id INTEGER NOT NULL REFERENCES ap_history_sets(set_id),
    ap_id TEXT NOT NULL REFERENCES aps(ap_id),
    version_id INTEGER NOT NULL REFERENCES ap_history_versions(version_id),
    PRIMARY KEY(set_id,ap_id)
);
CREATE INDEX ap_history_member_ap ON ap_history_members(ap_id,set_id);
CREATE TABLE run_history_frames (
    run_id INTEGER PRIMARY KEY REFERENCES runs(run_id),
    set_id INTEGER NOT NULL REFERENCES ap_history_sets(set_id),
    observed_at_utc TEXT NOT NULL,
    commit_sequence INTEGER NOT NULL CHECK(commit_sequence>=0)
);
CREATE INDEX run_history_frame_set ON run_history_frames(set_id,run_id);
CREATE VIEW historical_ap_rows AS
SELECT run_id,ap_id,generation_id,interval_id,metadata_version_id,facts_json,
       NULL AS history_observed_at_utc,NULL AS history_commit_sequence
FROM run_rows
UNION ALL
SELECT f.run_id,v.ap_id,v.generation_id,v.interval_id,v.metadata_version_id,v.facts_template_json,
       f.observed_at_utc,f.commit_sequence
FROM run_history_frames f JOIN ap_history_members m USING(set_id)
JOIN ap_history_versions v ON v.version_id=m.version_id AND v.ap_id=m.ap_id;
CREATE TRIGGER ap_history_versions_immutable BEFORE UPDATE ON ap_history_versions BEGIN SELECT RAISE(ABORT,'Historical version is immutable'); END;
CREATE TRIGGER ap_history_versions_no_delete BEFORE DELETE ON ap_history_versions BEGIN SELECT RAISE(ABORT,'Historical version is permanent'); END;
CREATE TRIGGER ap_history_versions_match BEFORE INSERT ON ap_history_versions
WHEN NOT EXISTS(SELECT 1 FROM generations g JOIN monitoring_intervals i ON i.generation_id=g.generation_id
                JOIN metadata_versions m ON m.ap_id=g.ap_id AND m.generation_id=g.generation_id
                WHERE g.generation_id=NEW.generation_id AND g.ap_id=NEW.ap_id AND i.interval_id=NEW.interval_id AND m.metadata_version_id=NEW.metadata_version_id)
BEGIN SELECT RAISE(ABORT,'Historical version identity mismatch'); END;
CREATE TRIGGER ap_history_sets_immutable BEFORE UPDATE ON ap_history_sets BEGIN SELECT RAISE(ABORT,'Historical membership is immutable'); END;
CREATE TRIGGER ap_history_sets_no_delete BEFORE DELETE ON ap_history_sets BEGIN SELECT RAISE(ABORT,'Historical membership is permanent'); END;
CREATE TRIGGER ap_history_members_immutable BEFORE UPDATE ON ap_history_members BEGIN SELECT RAISE(ABORT,'Historical member is immutable'); END;
CREATE TRIGGER ap_history_members_no_delete BEFORE DELETE ON ap_history_members BEGIN SELECT RAISE(ABORT,'Historical member is permanent'); END;
CREATE TRIGGER ap_history_members_sealed BEFORE INSERT ON ap_history_members
WHEN EXISTS(SELECT 1 FROM run_history_frames WHERE set_id=NEW.set_id)
BEGIN SELECT RAISE(ABORT,'Referenced historical membership is sealed'); END;
CREATE TRIGGER ap_history_members_match BEFORE INSERT ON ap_history_members
WHEN NOT EXISTS(SELECT 1 FROM ap_history_versions v JOIN aps a USING(ap_id) JOIN ap_history_sets s ON s.set_id=NEW.set_id
                WHERE v.version_id=NEW.version_id AND v.ap_id=NEW.ap_id AND a.dataset_id=s.dataset_id)
BEGIN SELECT RAISE(ABORT,'Historical member identity mismatch'); END;
CREATE TRIGGER run_history_frames_immutable BEFORE UPDATE ON run_history_frames BEGIN SELECT RAISE(ABORT,'Historical frame is immutable'); END;
CREATE TRIGGER run_history_frames_no_delete BEFORE DELETE ON run_history_frames BEGIN SELECT RAISE(ABORT,'Historical frame is permanent'); END;
CREATE TRIGGER run_history_frames_match BEFORE INSERT ON run_history_frames
WHEN NOT EXISTS(SELECT 1 FROM runs r JOIN ap_history_sets s ON s.set_id=NEW.set_id
                WHERE r.run_id=NEW.run_id AND r.dataset_id=s.dataset_id AND r.scope_version=s.scope_version AND r.kind=0 AND r.lifecycle=0
                AND s.member_count=(SELECT COUNT(*) FROM ap_history_members m WHERE m.set_id=s.set_id))
 OR EXISTS(SELECT 1 FROM run_rows r JOIN ap_history_members m ON m.set_id=NEW.set_id AND m.ap_id=r.ap_id WHERE r.run_id=NEW.run_id)
BEGIN SELECT RAISE(ABORT,'Historical frame identity or membership mismatch'); END;
-- Seal the legacy/detail side too: a later INSERT must not create a duplicate of
-- a compact member or add a new AP to any already finalized historical run.
CREATE TRIGGER run_rows_finalized_no_insert BEFORE INSERT ON run_rows
WHEN EXISTS(SELECT 1 FROM runs WHERE run_id=NEW.run_id AND lifecycle!=0)
 OR EXISTS(SELECT 1 FROM run_history_frames WHERE run_id=NEW.run_id)
BEGIN SELECT RAISE(ABORT,'Finalized historical rows are sealed'); END;
CREATE TRIGGER run_rows_no_delete BEFORE DELETE ON run_rows
BEGIN SELECT RAISE(ABORT,'Historical detail is permanent'); END;
