-- config.db, migration 0001. Ne jamais modifier après publication : ajouter une nouvelle migration.
CREATE TABLE Installation (
    Id           TEXT    NOT NULL PRIMARY KEY,
    InstalledAt  TEXT    NOT NULL,
    MachineName  TEXT    NOT NULL,
    KeyEntropy   BLOB    NOT NULL
);

CREATE TABLE Setting (
    Key        TEXT    NOT NULL PRIMARY KEY,
    Value      TEXT    NOT NULL,
    Version    INTEGER NOT NULL CHECK (Version >= 1),
    UpdatedAt  TEXT    NOT NULL,
    UpdatedBy  TEXT    NOT NULL
);

CREATE TABLE Principal (
    Sid         TEXT    NOT NULL PRIMARY KEY,
    Name        TEXT    NOT NULL,
    Kind        INTEGER NOT NULL CHECK (Kind IN (0, 1)),
    UpdatedAt   TEXT    NOT NULL
);

CREATE TABLE Folder (
    Id         TEXT    NOT NULL PRIMARY KEY,
    ParentId   TEXT    NULL REFERENCES Folder (Id),
    Name       TEXT    NOT NULL,
    CreatedAt  TEXT    NOT NULL
);

CREATE TABLE RoleAssignment (
    Id            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    PrincipalSid  TEXT    NOT NULL REFERENCES Principal (Sid),
    Role          INTEGER NOT NULL CHECK (Role BETWEEN 0 AND 4),
    ScopeKind     INTEGER NOT NULL DEFAULT 0 CHECK (ScopeKind BETWEEN 0 AND 2),
    ScopeId       TEXT    NOT NULL DEFAULT '',
    CreatedAt     TEXT    NOT NULL,
    CreatedBy     TEXT    NOT NULL,
    UNIQUE (PrincipalSid, Role, ScopeKind, ScopeId)
);

CREATE TABLE KeyVersion (
    Version    INTEGER NOT NULL PRIMARY KEY,
    CreatedAt  TEXT    NOT NULL,
    Active     INTEGER NOT NULL DEFAULT 0 CHECK (Active IN (0, 1))
);

CREATE UNIQUE INDEX UX_KeyVersion_Active ON KeyVersion (Active) WHERE Active = 1;

CREATE TABLE Secret (
    Id          TEXT    NOT NULL PRIMARY KEY,
    Usage       TEXT    NOT NULL,
    KeyVersion  INTEGER NOT NULL REFERENCES KeyVersion (Version),
    Nonce       BLOB    NOT NULL CHECK (length(Nonce) = 12),
    Ciphertext  BLOB    NOT NULL,
    Tag         BLOB    NOT NULL CHECK (length(Tag) = 16),
    CreatedAt   TEXT    NOT NULL,
    UpdatedAt   TEXT    NOT NULL
);
