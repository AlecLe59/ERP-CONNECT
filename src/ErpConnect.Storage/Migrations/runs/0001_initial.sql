-- runs.db, migration 0001. Schéma seulement en Phase 2 ; alimenté à partir de la Phase 3.
CREATE TABLE Run (
    Id            TEXT    NOT NULL PRIMARY KEY,
    FlowId        TEXT    NOT NULL,
    FlowVersion   INTEGER NOT NULL,
    Status        INTEGER NOT NULL CHECK (Status BETWEEN 0 AND 6),
    TriggeredBy   TEXT    NOT NULL,
    ParentRunId   TEXT    NULL REFERENCES Run (Id),
    CreatedAt     TEXT    NOT NULL,
    StartedAt     TEXT    NULL,
    EndedAt       TEXT    NULL
);

CREATE INDEX IX_Run_Flow_Status ON Run (FlowId, Status);

CREATE TABLE RunStatusHistory (
    Id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    RunId       TEXT    NOT NULL REFERENCES Run (Id),
    FromStatus  INTEGER NULL CHECK (FromStatus BETWEEN 0 AND 6),
    ToStatus    INTEGER NOT NULL CHECK (ToStatus BETWEEN 0 AND 6),
    At          TEXT    NOT NULL,
    Actor       TEXT    NOT NULL,
    Reason      TEXT    NULL
);

CREATE INDEX IX_RunStatusHistory_Run ON RunStatusHistory (RunId);

CREATE TRIGGER TR_RunStatusHistory_NoUpdate BEFORE UPDATE ON RunStatusHistory
BEGIN
    SELECT RAISE(ABORT, 'L''historique des statuts est en ajout seul.');
END;
