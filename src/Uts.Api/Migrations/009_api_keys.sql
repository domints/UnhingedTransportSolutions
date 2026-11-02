-- 009: API keys for automated (script) traffic.
-- Couriers log into the panel via Authentik OIDC; their scripts authenticate
-- with a bearer token instead. Keys are minted in the admin panel (one admin
-- sees/revokes only their own), stored as a SHA-256 hash — the full key is
-- shown exactly once at creation. Permissions are a text[] subset of the eight
-- grantable perms (keys.* is never grantable: keys can never manage keys).

CREATE TABLE api_keys (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name         TEXT        NOT NULL,          -- free-form label, shown in the panel
    created_by   TEXT        NOT NULL,          -- Authentik 'sub' of the minting admin
    key_hash     TEXT        NOT NULL UNIQUE,   -- SHA-256 hex of the full key
    key_prefix   TEXT        NOT NULL,          -- e.g. 'uts_sk_a1b2c3d4' for UI display
    permissions  TEXT[]      NOT NULL,          -- uses existing StringArrayTypeHandler
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_used_at TIMESTAMPTZ,
    revoked_at   TIMESTAMPTZ
);
