CREATE TABLE IF NOT EXISTS instance (
    singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
    id uuid NOT NULL, schema_version integer NOT NULL DEFAULT 1
);
INSERT INTO instance(singleton,id) VALUES(true,gen_random_uuid()) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS accounts (
    id uuid PRIMARY KEY, username text NOT NULL,
    normalized_username text UNIQUE NOT NULL, password_hash text NOT NULL,
    enabled boolean NOT NULL DEFAULT true, sync_epoch uuid NOT NULL DEFAULT gen_random_uuid(),
    history_sequence bigint NOT NULL DEFAULT 0, clipboard_sequence bigint NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS devices (
    account_id uuid NOT NULL REFERENCES accounts(id), id uuid NOT NULL, name text NOT NULL,
    last_seen timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(account_id,id)
);
CREATE TABLE IF NOT EXISTS sessions (
    id uuid PRIMARY KEY, account_id uuid NOT NULL REFERENCES accounts(id), device_id uuid NOT NULL,
    access_hash text UNIQUE NOT NULL, refresh_hash text UNIQUE NOT NULL, revoke_hash text UNIQUE NOT NULL,
    access_expires timestamptz NOT NULL, refresh_expires timestamptz NOT NULL,
    previous_refresh_hash text, previous_refresh_until timestamptz, previous_response text,
    revoked boolean NOT NULL DEFAULT false,
    FOREIGN KEY(account_id,device_id) REFERENCES devices(account_id,id)
);
CREATE TABLE IF NOT EXISTS records (
    account_id uuid NOT NULL REFERENCES accounts(id), id uuid NOT NULL,
    content_hash text NOT NULL, deleted boolean NOT NULL DEFAULT false, data jsonb NOT NULL,
    touched_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(account_id,id)
);
CREATE UNIQUE INDEX IF NOT EXISTS records_active_hash ON records(account_id,content_hash) WHERE NOT deleted;
CREATE TABLE IF NOT EXISTS aliases (
    account_id uuid NOT NULL REFERENCES accounts(id), alias_id uuid NOT NULL, record_id uuid NOT NULL,
    PRIMARY KEY(account_id,alias_id)
);
CREATE TABLE IF NOT EXISTS operations (
    account_id uuid NOT NULL REFERENCES accounts(id), id uuid NOT NULL, result jsonb NOT NULL,
    PRIMARY KEY(account_id,id)
);
CREATE TABLE IF NOT EXISTS changes (
    account_id uuid NOT NULL REFERENCES accounts(id), sequence bigint NOT NULL, data jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(account_id,sequence)
);
CREATE TABLE IF NOT EXISTS clipboard_events (
    account_id uuid NOT NULL REFERENCES accounts(id), id uuid NOT NULL, sequence bigint NOT NULL,
    record_id uuid NOT NULL, device_id uuid NOT NULL, kind text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(account_id,id)
);
CREATE TABLE IF NOT EXISTS uploads (
    id uuid PRIMARY KEY, account_id uuid NOT NULL REFERENCES accounts(id), kind text NOT NULL,
    byte_length bigint NOT NULL CHECK(byte_length>=0), sha256 text NOT NULL,
    completed boolean NOT NULL DEFAULT false, consumed boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(account_id,sha256,kind)
);
CREATE TABLE IF NOT EXISTS snapshots (
    id uuid PRIMARY KEY, account_id uuid NOT NULL REFERENCES accounts(id), cursor bigint NOT NULL,
    epoch uuid NOT NULL, records jsonb NOT NULL, expires_at timestamptz NOT NULL DEFAULT now()+interval '1 hour'
);
