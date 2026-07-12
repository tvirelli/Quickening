CREATE TABLE IF NOT EXISTS Files (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
    SizeBytes INTEGER NOT NULL,
    LastWriteTimeUtc TEXT NOT NULL,
    PartialHash BLOB,
    FullHash BLOB,
    PerceptualHash INTEGER,
    MimeCategory TEXT NOT NULL,
    FirstSeenUtc TEXT NOT NULL,
    LastSeenUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Files_SizeBytes ON Files (SizeBytes);
CREATE INDEX IF NOT EXISTS IX_Files_FullHash ON Files (FullHash);

CREATE TABLE IF NOT EXISTS ScanSessions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedUtc TEXT NOT NULL,
    CompletedUtc TEXT,
    RootPaths TEXT NOT NULL,
    FilterSnapshot TEXT,
    TotalFilesScanned INTEGER NOT NULL DEFAULT 0,
    TotalBytesScanned INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS TrashLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    OriginalPath TEXT NOT NULL,
    RecycleBinPath TEXT,
    SizeBytes INTEGER NOT NULL,
    DeletedUtc TEXT NOT NULL,
    ScanSessionId INTEGER REFERENCES ScanSessions (Id)
);

CREATE TABLE IF NOT EXISTS Stats (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    LifetimeBytesReclaimed INTEGER NOT NULL DEFAULT 0,
    LifetimeFilesRemoved INTEGER NOT NULL DEFAULT 0
);

INSERT OR IGNORE INTO Stats (Id, LifetimeBytesReclaimed, LifetimeFilesRemoved) VALUES (1, 0, 0);
