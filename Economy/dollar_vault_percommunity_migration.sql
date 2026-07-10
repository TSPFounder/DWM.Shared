-- dollar_vault_percommunity_migration.sql
-- Additive migration: adds PER-COMMUNITY Dollar Vault tables, layered on top of
-- economy_schema.sql. Does NOT modify economy_schema.sql, DollarVaultLedger, or
-- DollarVaultConfig -- those stay exactly as they are (a single global vault) in case
-- anything already depends on them. This adds the real per-community mechanism Day 10/11
-- actually need, alongside the original, not instead of it.
--
-- Design choice: a NEW table pair (CommunityDollarVaultLedger / CommunityDollarVaultConfig)
-- rather than adding a CommunityId column to the existing DollarVaultLedger/DollarVaultConfig.
-- Reasoning: those tables are frozen (economy_schema.sql, do not modify), and even if they
-- weren't, retrofitting a CommunityId column onto a table whose singleton row/global-balance
-- semantics something else might already read would be a breaking schema change to an
-- existing table, not an additive one -- exactly the risk Day 8's trade_requests_migration.sql
-- avoided by adding a new table instead of altering StoneLedger. Same reasoning applied here.
--
-- CascadingFailureThreshold is PER COMMUNITY, not shared/global -- confirmed, not assumed:
-- Day 11 needs to drive ONE community into cascading failure independently of the others.
-- A single shared threshold would make every community trip failure at the same balance
-- regardless of its own trajectory, which defeats the entire point of per-community
-- isolation -- the ledger being split by CommunityId wouldn't matter for the failure check
-- specifically if the trigger point were the same number for everyone.
--
-- Same convention as trade_requests_migration.sql: applied idempotently via
-- CREATE TABLE/INDEX IF NOT EXISTS + INSERT OR IGNORE for seed data, by
-- DollarVaultPerCommunityMigration.EnsureCreated (DWM.Shared/Economy/), called from
-- DollarVaultRepository's constructor.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS CommunityDollarVaultLedger (
    EntryId      TEXT PRIMARY KEY NOT NULL,
    CommunityId  TEXT NOT NULL REFERENCES Communities(CommunityId),
    Timestamp    TEXT NOT NULL,
    DeltaAmount  REAL NOT NULL,      -- positive = inflow, negative = outflow (same convention as DollarVaultLedger)
    Reason       TEXT
);
CREATE INDEX IF NOT EXISTS idx_communitydollarvaultledger_community ON CommunityDollarVaultLedger(CommunityId);

CREATE TABLE IF NOT EXISTS CommunityDollarVaultConfig (
    CommunityId                 TEXT PRIMARY KEY NOT NULL REFERENCES Communities(CommunityId),
    CascadingFailureThreshold   REAL NOT NULL
);

-- ====================================================================
-- SEED DATA -- placeholder MVP values, same spirit as economy_schema.sql's own seed
-- disclaimer ("quantities... are round starting numbers... purely as placeholders -- real
-- balance-tuning is a later pass"). Every community gets the SAME opening balance/threshold
-- as the original global vault ($5000 / $500) rather than inventing differentiated
-- per-community economics with no design basis to draw on -- see the outside-resources data
-- gap note in DollarVaultRepository.cs / this task's summary for the related, larger gap
-- this does NOT attempt to fill in. INSERT OR IGNORE keeps this idempotent across repeated
-- migration application.
-- ====================================================================

INSERT OR IGNORE INTO CommunityDollarVaultConfig (CommunityId, CascadingFailureThreshold) VALUES
    ('mountain', 500),
    ('hillside', 500),
    ('valley',   500),
    ('suburb',   500),
    ('city',     500);

INSERT OR IGNORE INTO CommunityDollarVaultLedger (EntryId, CommunityId, Timestamp, DeltaAmount, Reason) VALUES
    ('seed-vault-mountain', 'mountain', '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
    ('seed-vault-hillside', 'hillside', '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
    ('seed-vault-valley',   'valley',   '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
    ('seed-vault-suburb',   'suburb',   '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)'),
    ('seed-vault-city',     'city',     '2026-07-02T00:00:00Z', 5000, 'Initial per-community Dollar Vault funding (MVP placeholder, mirrors original global seed)');
