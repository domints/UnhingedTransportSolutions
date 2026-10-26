-- 008: multi-courier support.
-- Several people carry the uts-admins group in Authentik; each of them maps to
-- one Client row so the app knows who they are (name, telegram chat, parcels
-- addressed to them). auth_sub is the stable identity link (the IdP 'sub'
-- claim), e-mail would be a bad identity API. Trips remember which courier
-- actually travels, so recipient digests can name the right human being.

ALTER TABLE clients
    ADD COLUMN auth_sub TEXT NULL UNIQUE;

ALTER TABLE trips
    ADD COLUMN courier_client_id BIGINT NULL REFERENCES clients(id);

CREATE INDEX idx_trips_courier ON trips (courier_client_id);
