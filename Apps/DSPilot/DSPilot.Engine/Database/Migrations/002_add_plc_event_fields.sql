-- Migration 002: Add PLC Event Fields (Step 2)

-- Add PLC Tag mapping fields to dspCall
ALTER TABLE dspCall ADD COLUMN InTag TEXT;
ALTER TABLE dspCall ADD COLUMN OutTag TEXT;
ALTER TABLE dspCall ADD COLUMN LastStartAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastFinishAt TEXT;
ALTER TABLE dspCall ADD COLUMN LastDurationMs REAL;

-- Create plcTagLog table for PLC event history
CREATE TABLE IF NOT EXISTS plcTagLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    TagName TEXT NOT NULL,
    Value INTEGER NOT NULL,
    Timestamp TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_plc_tag_timestamp ON plcTagLog(TagName, Timestamp);
