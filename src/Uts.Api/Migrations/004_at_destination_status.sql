-- 004: add 'at_destination' status (courier is in the target city,
-- final delivery is being arranged). Sits between in_transit and delivered.

ALTER TABLE shipments DROP CONSTRAINT shipments_status_check;

ALTER TABLE shipments
    ADD CONSTRAINT shipments_status_check
    CHECK (status IN ('registered','picked_up','in_transit','at_destination','delivered','cancelled','lost'));
