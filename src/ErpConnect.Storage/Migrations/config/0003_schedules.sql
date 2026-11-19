-- config.db, migration 0003 (Phase 5) : planifications (une par flux au plus) et état du planificateur.
CREATE TABLE Schedule (
    FlowId             TEXT    NOT NULL PRIMARY KEY REFERENCES Flow (Id),
    Cron               TEXT    NOT NULL,
    TimeZone           TEXT    NOT NULL,
    Enabled            INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
    MaxRetries         INTEGER NOT NULL CHECK (MaxRetries BETWEEN 0 AND 10),
    RetryDelaySeconds  INTEGER NOT NULL CHECK (RetryDelaySeconds BETWEEN 10 AND 86400),
    Version            INTEGER NOT NULL CHECK (Version >= 1),
    UpdatedAt          TEXT    NOT NULL,
    UpdatedBy          TEXT    NOT NULL,
    NextRunAt          TEXT    NULL,
    LastRunId          TEXT    NULL,
    RetryAttempt       INTEGER NOT NULL DEFAULT 0,
    RetryAt            TEXT    NULL
);
