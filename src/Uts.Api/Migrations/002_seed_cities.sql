-- 002: seed starter set of Polish cities

INSERT INTO cities (code, name) VALUES
    ('WRO', 'Wrocław'),
    ('KRK', 'Kraków'),
    ('WAW', 'Warszawa'),
    ('POZ', 'Poznań'),
    ('GDN', 'Gdańsk'),
    ('GDY', 'Gdynia'),
    ('LDZ', 'Łódź'),
    ('KTW', 'Katowice'),
    ('LUB', 'Lublin'),
    ('SZC', 'Szczecin'),
    ('BIA', 'Białystok'),
    ('RZE', 'Rzeszów'),
    ('BYD', 'Bydgoszcz'),
    ('OLS', 'Olsztyn'),
    ('KIE', 'Kielce'),
    ('OPO', 'Opole')
ON CONFLICT (code) DO NOTHING;
