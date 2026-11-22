-- 010: courier custody (handover) + the 'courier' timeline event.
-- trips.trip_id says how a parcel travels; assigned_courier_client_id says who
-- physically holds it right now. The two are deliberately independent: a parcel
-- can sit with a courier who has no trip planned yet (handover), and a parcel
-- stays in the custody of its courier after the trip is completed/cancelled.
-- Any clients row can be a custodian (middlemen included) — custody is a fact,
-- not a permission.

ALTER TABLE shipments
    ADD COLUMN assigned_courier_client_id BIGINT NULL REFERENCES clients (id);

CREATE INDEX idx_shipments_courier ON shipments (assigned_courier_client_id);

-- Handovers get their own event type so the timeline can style them apart
-- (hidden from the public pre-PIN timeline, same rule as 'trip').
ALTER TABLE shipment_events DROP CONSTRAINT IF EXISTS shipment_events_type_check;
ALTER TABLE shipment_events
    ADD CONSTRAINT shipment_events_type_check
    CHECK (type IN ('created', 'status', 'trip', 'note', 'courier'));
