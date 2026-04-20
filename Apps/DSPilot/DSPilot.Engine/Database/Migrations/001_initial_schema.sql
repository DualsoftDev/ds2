-- Migration 001: Initial Schema (Step 0)

CREATE TABLE IF NOT EXISTS dspFlow (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FlowName TEXT NOT NULL UNIQUE,
    State TEXT DEFAULT 'Ready',
    ActiveCallCount INTEGER DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS dspCall (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CallName TEXT NOT NULL UNIQUE,
    FlowName TEXT NOT NULL,
    State TEXT DEFAULT 'Ready',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    FOREIGN KEY (FlowName) REFERENCES dspFlow(FlowName)
);

CREATE INDEX IF NOT EXISTS idx_call_flow ON dspCall(FlowName);
