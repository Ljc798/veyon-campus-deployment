#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${PGDATABASE:-}" ]]; then
    printf 'Set PGDATABASE to a disposable, empty PostgreSQL database before running this check.\n' >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
psql_flags=(-X -v ON_ERROR_STOP=1)
anonymous_package_id='11111111-1111-4111-8111-111111111111'
registered_package_id='22222222-2222-4222-8222-222222222222'
inactive_package_id='33333333-3333-4333-8333-333333333333'
withdrawn_package_id='44444444-4444-4444-8444-444444444444'
teacher_release_id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
student_release_id='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'

psql "${psql_flags[@]}" <<'SQL'
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        CREATE ROLE anon;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        CREATE ROLE authenticated;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
        CREATE ROLE service_role;
    END IF;
END;
$roles$;
CREATE SCHEMA auth;
CREATE SCHEMA storage;
CREATE FUNCTION auth.uid() RETURNS text
LANGUAGE sql STABLE
AS $auth$ SELECT NULL::text $auth$;
GRANT USAGE ON SCHEMA auth TO PUBLIC;
GRANT EXECUTE ON FUNCTION auth.uid() TO PUBLIC;
CREATE TABLE storage.buckets (
    id text PRIMARY KEY,
    name text NOT NULL,
    public boolean NOT NULL,
    file_size_limit bigint NOT NULL,
    allowed_mime_types text[]
);
CREATE TABLE public.campuses (
    id bigint PRIMARY KEY,
    status text NOT NULL
);
CREATE TABLE public.admin_profiles (
    user_id text PRIMARY KEY,
    role text NOT NULL
);
GRANT SELECT ON public.admin_profiles TO authenticated;
CREATE TABLE public.deployment_packages (
    package_id uuid PRIMARY KEY,
    campus_id bigint REFERENCES public.campuses(id),
    status text NOT NULL
);
INSERT INTO public.campuses (id, status) VALUES (1, 'active'), (2, 'inactive');
INSERT INTO public.deployment_packages (package_id, campus_id, status) VALUES
    ('11111111-1111-4111-8111-111111111111', NULL, 'published'),
    ('22222222-2222-4222-8222-222222222222', 1, 'published'),
    ('33333333-3333-4333-8333-333333333333', 2, 'published'),
    ('44444444-4444-4444-8444-444444444444', NULL, 'withdrawn');
SQL

psql "${psql_flags[@]}" \
    --file "$repo_root/cloudbase/migrations/20260930150000_create_application_releases_and_teacher_heartbeat.sql" \
    >/dev/null

psql "${psql_flags[@]}" <<'SQL'
SET ROLE service_role;
SELECT public.publish_application_release_v1(
    'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'TeacherConsole', '0.4.41',
    4096, repeat('A', 64), repeat('B', 344)
);
SELECT public.publish_application_release_v1(
    'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'StudentSetup', '1.10.1',
    8192, repeat('C', 64), repeat('D', 344)
);
SELECT public.record_campus_teacher_heartbeat_v1(
    (timezone('Asia/Hong_Kong', statement_timestamp()))::date,
    repeat('E', 64), '11111111-1111-4111-8111-111111111111', repeat('F', 64),
    '0.4.40', '1.10.0', 24
);
SELECT public.record_campus_teacher_heartbeat_v1(
    (timezone('Asia/Hong_Kong', statement_timestamp()))::date,
    repeat('A', 64), '11111111-1111-4111-8111-111111111111', repeat('F', 64),
    '0.4.41', '1.10.1', 26
);
SELECT public.record_campus_teacher_heartbeat_v1(
    (timezone('Asia/Hong_Kong', statement_timestamp()))::date,
    repeat('B', 64), '22222222-2222-4222-8222-222222222222', repeat('C', 64),
    '0.4.41', '1.10.1', 18
);
RESET ROLE;
SQL

psql "${psql_flags[@]}" <<'SQL'
DO $verify$
DECLARE
    result record;
    row_count integer;
    bucket_is_public boolean;
    bucket_size bigint;
    bucket_mime_types text[];
    rejected boolean;
    hk_day date := (timezone('Asia/Hong_Kong', statement_timestamp()))::date;
BEGIN
    SELECT count(*) INTO row_count FROM public.application_releases;
    IF row_count <> 2 THEN
        RAISE EXCEPTION 'Expected two role-isolated release rows, got %', row_count;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.application_releases
        WHERE release_id = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
          AND role = 'TeacherConsole'
          AND product = 'VeyonCampus.TeacherConsole'
          AND file_name = 'VeyonCampus-Teacher-Setup-0.4.41-win-x64.exe'
          AND object_key = 'releases/TeacherConsole/win-x64/aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa.exe'
          AND signature_algorithm = 'RSA-PSS-SHA256'
          AND status = 'published'
    ) THEN
        RAISE EXCEPTION 'Teacher release metadata is inconsistent';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM public.application_releases
        WHERE release_id = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
          AND role = 'StudentSetup'
          AND product = 'VeyonCampus.StudentSetup'
          AND file_name = 'VeyonCampus-Student-Setup-1.10.1-win-x64.exe'
          AND object_key = 'releases/StudentSetup/win-x64/bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb.exe'
    ) THEN
        RAISE EXCEPTION 'Student release metadata is inconsistent';
    END IF;
    SELECT public, file_size_limit, allowed_mime_types
      INTO bucket_is_public, bucket_size, bucket_mime_types
      FROM storage.buckets WHERE id = 'application-release-artifacts';
    IF bucket_is_public IS DISTINCT FROM false OR bucket_size <> 536870912 OR
       NOT ARRAY[
           'application/octet-stream',
           'application/vnd.microsoft.portable-executable',
           'application/x-msdownload'
       ]::text[] <@ bucket_mime_types THEN
        RAISE EXCEPTION 'Release artifact bucket is not private with the expected size limit';
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_schema = 'public'
           AND table_name = 'campus_daily_teacher_heartbeats'
           AND column_name IN (
               'publisher_instance_id', 'teacher_phone_last4', 'computer_name', 'ip_address'
           )
    ) THEN
        RAISE EXCEPTION 'Teacher heartbeat table contains a raw identity or network field';
    END IF;

    SELECT count(*) INTO row_count FROM public.campus_daily_teacher_heartbeats
     WHERE day_hkt = hk_day AND campus_identity_digest = repeat('F', 64);
    IF row_count <> 1 THEN
        RAISE EXCEPTION 'Repeated anonymous heartbeat did not deduplicate to one row';
    END IF;
    SELECT * INTO result FROM public.campus_daily_teacher_heartbeats
     WHERE day_hkt = hk_day AND campus_identity_digest = repeat('F', 64);
    IF result.campus_id IS NOT NULL OR result.package_id <> '11111111-1111-4111-8111-111111111111'
       OR result.publisher_digest <> repeat('A', 64)
       OR result.teacher_version <> '0.4.41' OR result.student_version <> '1.10.1'
       OR result.configured_computer_count <> 26 THEN
        RAISE EXCEPTION 'Anonymous heartbeat upsert did not keep the latest aggregate snapshot';
    END IF;
    SELECT * INTO result FROM public.campus_daily_teacher_heartbeats
     WHERE day_hkt = hk_day AND campus_identity_digest = repeat('C', 64);
    IF result.campus_id <> 1 OR result.package_id <> '22222222-2222-4222-8222-222222222222' THEN
        RAISE EXCEPTION 'Registered campus heartbeat did not resolve the active campus';
    END IF;

    IF has_table_privilege('anon', 'public.application_releases', 'SELECT') OR
       has_table_privilege('authenticated', 'public.application_releases', 'SELECT') OR
       has_table_privilege('anon', 'public.campus_daily_teacher_heartbeats', 'SELECT') OR
       has_function_privilege('anon',
           'public.publish_application_release_v1(uuid,text,text,bigint,text,text)', 'EXECUTE') OR
       has_function_privilege('anon',
           'public.record_campus_teacher_heartbeat_v1(date,text,uuid,text,text,text,integer)', 'EXECUTE') OR
       has_function_privilege('authenticated',
           'public.record_campus_teacher_heartbeat_v1(date,text,uuid,text,text,text,integer)', 'EXECUTE') THEN
        RAISE EXCEPTION 'Anonymous or authenticated role has a forbidden release/heartbeat grant';
    END IF;
    IF NOT has_function_privilege('service_role',
           'public.publish_application_release_v1(uuid,text,text,bigint,text,text)', 'EXECUTE') OR
       NOT has_function_privilege('service_role',
           'public.record_campus_teacher_heartbeat_v1(date,text,uuid,text,text,text,integer)', 'EXECUTE') THEN
        RAISE EXCEPTION 'service_role is missing a required release/heartbeat RPC grant';
    END IF;

    rejected := false;
    BEGIN
        PERFORM public.record_campus_teacher_heartbeat_v1(
            hk_day - 1, repeat('A', 64), '11111111-1111-4111-8111-111111111111',
            repeat('F', 64), '0.4.41', '1.10.1', 26
        );
    EXCEPTION WHEN OTHERS THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Heartbeat RPC accepted a non-current Hong Kong date';
    END IF;

    rejected := false;
    BEGIN
        PERFORM public.record_campus_teacher_heartbeat_v1(
            hk_day, repeat('A', 64), '33333333-3333-4333-8333-333333333333',
            repeat('1', 64), '0.4.41', '1.10.1', 26
        );
    EXCEPTION WHEN OTHERS THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Heartbeat RPC accepted a package mapped to an inactive campus';
    END IF;

    rejected := false;
    BEGIN
        PERFORM public.publish_application_release_v1(
            'cccccccc-cccc-4ccc-8ccc-cccccccccccc', 'StudentSetup', '01.10.1',
            8192, repeat('C', 64), repeat('D', 344)
        );
    EXCEPTION WHEN OTHERS THEN
        rejected := true;
    END;
    IF NOT rejected THEN
        RAISE EXCEPTION 'Release RPC accepted a non-canonical SemVer';
    END IF;
END;
$verify$;
SQL

authenticated_heartbeat_count="$(psql "${psql_flags[@]}" -qAt \
    -c 'SET ROLE authenticated' \
    -c 'SELECT count(*) FROM public.campus_daily_teacher_heartbeats;')"
if [[ "$authenticated_heartbeat_count" != '0' ]]; then
    printf 'Authenticated non-admin role can read %s heartbeat rows.\n' \
        "$authenticated_heartbeat_count" >&2
    exit 1
fi

service_release_count="$(psql "${psql_flags[@]}" -qAt \
    -c 'SET ROLE service_role' \
    -c 'SELECT count(*) FROM public.application_releases;')"
if [[ "$service_release_count" != '2' ]]; then
    printf 'service_role can read only %s application release rows.\n' \
        "$service_release_count" >&2
    exit 1
fi

if psql "${psql_flags[@]}" -qAt \
    -c 'SET ROLE anon' \
    -c 'SELECT count(*) FROM public.campus_daily_teacher_heartbeats;' \
    >/dev/null 2>&1; then
    printf 'anon unexpectedly queried Teacher heartbeat aggregates.\n' >&2
    exit 1
fi

if psql "${psql_flags[@]}" -qAt \
    -c 'SET ROLE anon' \
    -c "SELECT public.publish_application_release_v1('$teacher_release_id', 'TeacherConsole', '0.4.42', 4096, repeat('A', 64), repeat('B', 344));" \
    >/dev/null 2>&1; then
    printf 'anon unexpectedly published an application release.\n' >&2
    exit 1
fi

printf 'PostgreSQL release metadata, private bucket, Teacher heartbeat deduplication, and ACL checks passed.\n'
