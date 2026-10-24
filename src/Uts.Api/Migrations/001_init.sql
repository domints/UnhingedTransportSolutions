-- 001: initial schema

CREATE SEQUENCE shipment_number_seq START 1;

CREATE TABLE clients (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name             TEXT NOT NULL,
    contact          TEXT,
    telegram_chat_id TEXT,
    notes            TEXT,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE cities (
    code   CHAR(3) PRIMARY KEY,
    name   TEXT NOT NULL,
    active BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE trips (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title            TEXT NOT NULL,
    origin_code      CHAR(3) REFERENCES cities (code),
    destination_code CHAR(3) REFERENCES cities (code),
    planned_date     DATE,
    status           TEXT NOT NULL DEFAULT 'planned'
                     CHECK (status IN ('planned', 'completed', 'cancelled')),
    notes            TEXT,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE shipments (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tracking_number  TEXT NOT NULL UNIQUE,
    sender_id        BIGINT NOT NULL REFERENCES clients (id),
    recipient_id     BIGINT NOT NULL REFERENCES clients (id),
    origin_code      CHAR(3) REFERENCES cities (code),
    destination_code CHAR(3) NOT NULL REFERENCES cities (code),
    description      TEXT NOT NULL,
    transport_mode   TEXT NOT NULL DEFAULT 'unknown'
                     CHECK (transport_mode IN ('train', 'car', 'unknown')),
    status           TEXT NOT NULL DEFAULT 'registered'
                     CHECK (status IN ('registered','picked_up','in_transit','delivered','cancelled','lost')),
    pin_salt         TEXT NOT NULL,
    pin_hash         TEXT NOT NULL,
    notes            TEXT,
    trip_id          BIGINT REFERENCES trips (id),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    delivered_at     TIMESTAMPTZ
);

CREATE INDEX idx_shipments_status      ON shipments (status);
CREATE INDEX idx_shipments_destination ON shipments (destination_code);
CREATE INDEX idx_shipments_trip        ON shipments (trip_id);

CREATE TABLE shipment_events (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    shipment_id BIGINT NOT NULL REFERENCES shipments (id) ON DELETE CASCADE,
    type        TEXT NOT NULL CHECK (type IN ('created', 'status', 'trip', 'note')),
    message     TEXT NOT NULL,
    source      TEXT NOT NULL DEFAULT 'auto' CHECK (source IN ('auto', 'manual')),
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_events_shipment ON shipment_events (shipment_id, created_at DESC);
