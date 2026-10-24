-- 005: intermediate stops for trips.
-- A trip WRO→KRK may also carry parcels for cities passed on the way
-- (e.g. KTW, OPW). Assignment checks the shipment destination against
-- the trip destination AND all stops.

CREATE TABLE trip_stops (
    trip_id    BIGINT NOT NULL REFERENCES trips (id) ON DELETE CASCADE,
    city_code  TEXT   NOT NULL REFERENCES cities (code) ON UPDATE CASCADE,
    position   INT    NOT NULL DEFAULT 0,
    PRIMARY KEY (trip_id, city_code)
);

CREATE INDEX idx_trip_stops_city ON trip_stops (city_code);
