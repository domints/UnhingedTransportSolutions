-- 006: trip transport mode + trip templates.
-- A template stores a recurring route (origin -> stops -> destination)
-- so regular round-trips don't have to be typed in every time.

ALTER TABLE trips
    ADD COLUMN transport_mode TEXT NOT NULL DEFAULT 'unknown'
        CHECK (transport_mode IN ('train', 'car', 'unknown'));

CREATE TABLE trip_templates (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title            TEXT NOT NULL,
    origin_code      CHAR(3) REFERENCES cities (code),
    destination_code CHAR(3) REFERENCES cities (code),
    stops            TEXT[] NOT NULL DEFAULT '{}',
    transport_mode   TEXT NOT NULL DEFAULT 'unknown'
                     CHECK (transport_mode IN ('train', 'car', 'unknown')),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
