-- Phase 7 : licence importée (une seule active). Le contenu est le fichier signé tel que reçu ; il ne contient aucun secret.
CREATE TABLE License (
    Id          INTEGER PRIMARY KEY CHECK (Id = 1),
    Content     TEXT NOT NULL,
    LicenseId   TEXT NOT NULL,
    ImportedAt  TEXT NOT NULL,
    ImportedBy  TEXT NOT NULL
);
