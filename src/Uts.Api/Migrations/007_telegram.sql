-- 007: telegram bot support.
-- Recipients get status notifications through an outbox queue (transactionally
-- written by the endpoints, drained by the background sender). Claim codes
-- bind Telegram chats to clients; each client can mute notifications.

ALTER TABLE clients
    ADD COLUMN telegram_notifications BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN telegram_claim_code    TEXT NULL,
    ADD COLUMN telegram_claim_expires TIMESTAMPTZ NULL;

CREATE TABLE telegram_outbox (
    id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    chat_id       TEXT NOT NULL,
    text          TEXT NOT NULL,
    keyboard_json TEXT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    sent_at       TIMESTAMPTZ NULL,
    attempts      INT NOT NULL DEFAULT 0,
    last_attempt  TIMESTAMPTZ NULL,
    last_error    TEXT NULL
);

CREATE INDEX idx_outbox_unsent ON telegram_outbox (id) WHERE sent_at IS NULL;
