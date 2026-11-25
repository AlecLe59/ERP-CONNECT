-- config.db, migration 0002 (Phase 3) : connexions et flux versionnés.
CREATE TABLE Connection (
    Id            TEXT    NOT NULL PRIMARY KEY,
    Name          TEXT    NOT NULL UNIQUE,
    ConnectorId   TEXT    NOT NULL,
    SettingsJson  TEXT    NOT NULL,
    SecretFields  TEXT    NOT NULL DEFAULT '',
    Version       INTEGER NOT NULL CHECK (Version >= 1),
    UpdatedAt     TEXT    NOT NULL,
    UpdatedBy     TEXT    NOT NULL
);

CREATE TABLE Flow (
    Id                TEXT    NOT NULL PRIMARY KEY,
    Name              TEXT    NOT NULL UNIQUE,
    Description       TEXT    NOT NULL DEFAULT '',
    DraftVersion      INTEGER NOT NULL CHECK (DraftVersion >= 1),
    PublishedVersion  INTEGER NULL,
    UpdatedAt         TEXT    NOT NULL,
    UpdatedBy         TEXT    NOT NULL
);

-- Chaque enregistrement crée une version ; une version n'est jamais modifiée (seule sa publication est tracée).
CREATE TABLE FlowVersion (
    FlowId          TEXT    NOT NULL REFERENCES Flow (Id),
    Version         INTEGER NOT NULL,
    DefinitionJson  TEXT    NOT NULL,
    CreatedAt       TEXT    NOT NULL,
    CreatedBy       TEXT    NOT NULL,
    PublishedAt     TEXT    NULL,
    PublishedBy     TEXT    NULL,
    PRIMARY KEY (FlowId, Version)
);

CREATE TRIGGER TR_FlowVersion_Immutable BEFORE UPDATE OF DefinitionJson, FlowId, Version, CreatedAt, CreatedBy ON FlowVersion
BEGIN
    SELECT RAISE(ABORT, 'Une version de flux est immuable.');
END;

CREATE TRIGGER TR_FlowVersion_NoDelete BEFORE DELETE ON FlowVersion
BEGIN
    SELECT RAISE(ABORT, 'Une version de flux ne se supprime pas.');
END;
