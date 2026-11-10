-- Fixture for load-tests/sale.js: N confirmed buyers loadbuyer00001@ticketrush.test ... with the dev seed's password.
--
-- Clones the dev seed account customer@ticketrush.dev (password Dev@12345, email confirmed, role Customer), so no
-- password hashing happens here and no test-only endpoint is needed. Idempotent: existing buyers are left alone.
-- Dev/demo databases only; the migrations service seeds customer@ticketrush.dev when Migrations__SeedDevData=true.
--
--   psql -v buyers=500 -f load-tests/sql/seed-buyers.sql
INSERT INTO users (id, access_failed_count, avatar_url, concurrency_stamp, created_at, email, email_confirmed, full_name,
                   lockout_enabled, lockout_end, normalized_email, normalized_user_name, password_hash, phone_number,
                   phone_number_confirmed, security_stamp, two_factor_enabled, updated_at, user_name)
SELECT gen_random_uuid(), 0, NULL, gen_random_uuid()::text, now(), b.email, true, 'Load Buyer ' || b.n,
       false, NULL, upper(b.email), upper(b.email), c.password_hash, NULL,
       false, gen_random_uuid()::text, false, now(), b.email
FROM users c
CROSS JOIN LATERAL (
    SELECT n, 'loadbuyer' || lpad(n::text, 5, '0') || '@ticketrush.test' AS email
    FROM generate_series(1, :buyers) AS n
) b
WHERE c.normalized_email = 'CUSTOMER@TICKETRUSH.DEV'
  AND NOT EXISTS (SELECT 1 FROM users u WHERE u.normalized_email = upper(b.email));

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, ur.role_id
FROM users u
JOIN users c ON c.normalized_email = 'CUSTOMER@TICKETRUSH.DEV'
JOIN user_roles ur ON ur.user_id = c.id
WHERE u.normalized_email LIKE 'LOADBUYER%@TICKETRUSH.TEST'
  AND NOT EXISTS (SELECT 1 FROM user_roles x WHERE x.user_id = u.id AND x.role_id = ur.role_id);

SELECT count(*) AS load_buyers FROM users WHERE normalized_email LIKE 'LOADBUYER%@TICKETRUSH.TEST';
