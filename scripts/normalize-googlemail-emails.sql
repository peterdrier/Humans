-- Run ONCE before deploying the googlemail normalization code change.
-- Canonicalizes @googlemail.com → @gmail.com in all email columns.
-- Safe to re-run (idempotent).

BEGIN;

-- users table: email, normalized_email, user_name, normalized_user_name
UPDATE users
SET email = REPLACE(email, '@googlemail.com', '@gmail.com'),
    normalized_email = UPPER(REPLACE(email, '@googlemail.com', '@gmail.com')),
    user_name = REPLACE(user_name, '@googlemail.com', '@gmail.com'),
    normalized_user_name = UPPER(REPLACE(user_name, '@googlemail.com', '@gmail.com'))
WHERE email ILIKE '%@googlemail.com';

-- user_emails table
UPDATE user_emails
SET email = REPLACE(email, '@googlemail.com', '@gmail.com')
WHERE email ILIKE '%@googlemail.com';

COMMIT;
