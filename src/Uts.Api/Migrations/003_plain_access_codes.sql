-- 003: store access codes in plaintext (they are shared openly with recipients)
-- Old hashed codes cannot be recovered — shipments get fresh random ones.

ALTER TABLE shipments ADD COLUMN pin TEXT;

UPDATE shipments
SET pin = lpad((floor(random() * 900000) + 100000)::int::text, 6, '0');

ALTER TABLE shipments ALTER COLUMN pin SET NOT NULL;

ALTER TABLE shipments DROP COLUMN pin_salt;
ALTER TABLE shipments DROP COLUMN pin_hash;
