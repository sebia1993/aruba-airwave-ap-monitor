CREATE TABLE source_checkpoints (
    dataset_id TEXT NOT NULL REFERENCES dataset(dataset_id),
    source_name TEXT NOT NULL,
    position INTEGER NOT NULL CHECK(position >= 0),
    run_id INTEGER NOT NULL REFERENCES runs(run_id),
    PRIMARY KEY(dataset_id, source_name)
);
