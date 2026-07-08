-- trade_requests_migration.sql
-- Additive migration: adds the TradeRequests table for trade lifecycle support
-- (Proposed -> Settled/Cancelled), layered on top of economy_schema.sql. Does NOT modify
-- economy_schema.sql or any table it defines. StoneLedger stays untouched and append-only:
-- a TradeRequests row only produces a StoneLedger row once TradeRequestRepository.
-- SettleProposedTrade actually settles it. A Cancelled or still-Proposed row never does.
--
-- Applied by TradeRequestsMigration.EnsureCreated (DWM.Shared/Economy/), which runs this
-- exact DDL (kept in lockstep, same convention economy_schema.sql / EconomySeeder.cs already
-- use for each other) via CREATE TABLE/INDEX IF NOT EXISTS, so it's safe to apply repeatedly
-- against an economy.db that EconomySeeder already seeded.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS TradeRequests (
    RequestId        TEXT PRIMARY KEY NOT NULL,
    FromCommunityId  TEXT NOT NULL REFERENCES Communities(CommunityId),
    ToCommunityId    TEXT NOT NULL REFERENCES Communities(CommunityId),
    Amount           REAL NOT NULL,
    ResourceId       TEXT REFERENCES Resources(ResourceId),
    Quantity         REAL,
    Memo             TEXT,
    Status           TEXT NOT NULL CHECK (Status IN ('Proposed', 'Settled', 'Cancelled')) DEFAULT 'Proposed',
    CreatedAt        TEXT NOT NULL,
    ResolvedAt       TEXT
);
CREATE INDEX IF NOT EXISTS idx_traderequests_status ON TradeRequests(Status);
