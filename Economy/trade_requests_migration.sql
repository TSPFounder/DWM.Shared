-- trade_requests_migration.sql
-- Additive migration: adds the TradeRequests table for trade lifecycle support
-- (Proposed -> Settling -> Settled, or Proposed -> Cancelled), layered on top of
-- economy_schema.sql. Does NOT modify economy_schema.sql or any table it defines.
-- StoneLedger stays untouched and append-only: a TradeRequests row only produces a
-- StoneLedger row once TradeRequestRepository.SettleProposedTrade actually settles it. A
-- Cancelled or still-Proposed row never does.
--
-- "Settling" is a transient status, never a default/initial value — it exists purely as the
-- target of SettleProposedTrade's compare-and-swap claim (UPDATE ... WHERE Status='Proposed'
-- immediately before the StoneLedger write), which closes a race where a crash between the
-- ledger write and the Settled update would otherwise leave the request looking like it's
-- still Proposed, letting a naive retry double-append to StoneLedger. If the process dies
-- while a request is Settling, it stays stuck there — neither SettleProposedTrade nor
-- CancelProposedTrade will act on a non-Proposed request, so a stuck Settling row requires
-- manual investigation/recovery rather than resolving itself. That's a deliberate tradeoff:
-- a stuck-but-safe row beats a silently duplicated ledger entry.
--
-- Note: CREATE TABLE IF NOT EXISTS means an economy.db whose TradeRequests table was already
-- created under the OLD two-state CHECK constraint (Proposed/Settled/Cancelled only) will NOT
-- pick up the new constraint automatically — SQLite has no ALTER TABLE for CHECK constraints.
-- Drop and recreate TradeRequests on any such pre-existing database.
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
    Status           TEXT NOT NULL CHECK (Status IN ('Proposed', 'Settling', 'Settled', 'Cancelled')) DEFAULT 'Proposed',
    CreatedAt        TEXT NOT NULL,
    ResolvedAt       TEXT
);
CREATE INDEX IF NOT EXISTS idx_traderequests_status ON TradeRequests(Status);
