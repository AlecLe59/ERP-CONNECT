-- runs.db, migration 0003 (Phase 5) : nature de l'erreur, registre anti-doublons, alertes.
ALTER TABLE Run ADD COLUMN ErrorKind TEXT NULL CHECK (ErrorKind IN ('transient', 'permanent', 'rejects', 'internal'));

-- Clé métier hachée (SHA-256) : aucune donnée métier en clair dans le registre.
CREATE TABLE DedupKey (
    FlowId   TEXT NOT NULL,
    KeyHash  TEXT NOT NULL,
    RunId    TEXT NOT NULL,
    At       TEXT NOT NULL,
    PRIMARY KEY (FlowId, KeyHash)
) WITHOUT ROWID;

CREATE TABLE Alert (
    Id              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    FlowId          TEXT    NOT NULL,
    RunId           TEXT    NULL,
    Severity        TEXT    NOT NULL CHECK (Severity IN ('warning', 'error')),
    Message         TEXT    NOT NULL,
    CreatedAt       TEXT    NOT NULL,
    AcknowledgedAt  TEXT    NULL,
    AcknowledgedBy  TEXT    NULL
);

CREATE INDEX IX_Alert_Open ON Alert (AcknowledgedAt, CreatedAt);
