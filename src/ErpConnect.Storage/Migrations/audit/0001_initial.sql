-- audit.db, migration 0001. Journal chaîné : chaque entrée porte l'empreinte de la précédente.
CREATE TABLE AuditEvent (
    Sequence       INTEGER NOT NULL PRIMARY KEY,
    Timestamp      TEXT    NOT NULL,
    ActorSid       TEXT    NOT NULL,
    ActorName      TEXT    NOT NULL,
    Action         TEXT    NOT NULL,
    Target         TEXT    NOT NULL,
    Outcome        TEXT    NOT NULL,
    Details        TEXT    NULL,
    CorrelationId  TEXT    NOT NULL,
    PreviousHash   TEXT    NOT NULL,
    Hash           TEXT    NOT NULL UNIQUE
);

CREATE INDEX IX_AuditEvent_Action ON AuditEvent (Action);
CREATE INDEX IX_AuditEvent_Timestamp ON AuditEvent (Timestamp);

-- Le service n'efface ni ne modifie jamais une entrée. La purge par rétention (phase ultérieure)
-- passera par une procédure dédiée qui conserve une ancre de chaîne.
CREATE TRIGGER TR_AuditEvent_NoUpdate BEFORE UPDATE ON AuditEvent
BEGIN
    SELECT RAISE(ABORT, 'Le journal d''audit est en ajout seul.');
END;

CREATE TRIGGER TR_AuditEvent_NoDelete BEFORE DELETE ON AuditEvent
BEGIN
    SELECT RAISE(ABORT, 'Le journal d''audit est en ajout seul.');
END;
