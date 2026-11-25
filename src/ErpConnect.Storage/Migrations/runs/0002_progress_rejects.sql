-- runs.db, migration 0002 (Phase 3) : compteurs, point de reprise, rejets et journal d'exécution.
ALTER TABLE Run ADD COLUMN ReadCount     INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Run ADD COLUMN WrittenCount  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Run ADD COLUMN RejectedCount INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Run ADD COLUMN SkippedCount  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Run ADD COLUMN Checkpoint    TEXT    NULL;
ALTER TABLE Run ADD COLUMN Error         TEXT    NULL;
ALTER TABLE Run ADD COLUMN TriggerKind   TEXT    NULL;

CREATE INDEX IX_Run_CreatedAt ON Run (CreatedAt);

CREATE TABLE RunReject (
    Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    RunId        TEXT    NOT NULL REFERENCES Run (Id),
    BatchNumber  INTEGER NOT NULL,
    RecordIndex  INTEGER NOT NULL,
    Stage        TEXT    NOT NULL CHECK (Stage IN ('read', 'mapping', 'write')),
    Reason       TEXT    NOT NULL,
    RecordJson   TEXT    NULL,
    At           TEXT    NOT NULL
);

CREATE INDEX IX_RunReject_Run ON RunReject (RunId);

CREATE TABLE RunLog (
    Id       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    RunId    TEXT    NOT NULL REFERENCES Run (Id),
    At       TEXT    NOT NULL,
    Level    TEXT    NOT NULL CHECK (Level IN ('info', 'warning', 'error')),
    Message  TEXT    NOT NULL
);

CREATE INDEX IX_RunLog_Run ON RunLog (RunId);
