-- Migrazione per il Worker di prova: limitazione tentativi di accesso/attivazione.
-- Eseguire una sola volta nella Console D1 di pallamano-operatori-test.

CREATE TABLE IF NOT EXISTS access_rate_limits (
    rate_key TEXT PRIMARY KEY,
    attempts INTEGER NOT NULL DEFAULT 0,
    window_started_at INTEGER NOT NULL,
    blocked_until INTEGER
);

CREATE INDEX IF NOT EXISTS idx_access_rate_limits_blocked
ON access_rate_limits(blocked_until);
