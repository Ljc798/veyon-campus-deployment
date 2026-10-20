#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${PGDATABASE:-}" || "$PGDATABASE" != veyon_file_update_catalog_test* ]]; then
    printf 'Set PGDATABASE to a disposable database named veyon_file_update_catalog_test* before running this check.\n' >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
psql_flags=(-X -v ON_ERROR_STOP=1)

psql "${psql_flags[@]}" <<'SQL'
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        CREATE ROLE anon NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        CREATE ROLE authenticated NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
        CREATE ROLE service_role NOLOGIN;
    END IF;
END;
$roles$;
SQL

psql "${psql_flags[@]}" \
    --file "$repo_root/cloudbase/migrations/20261021140000_create_file_update_release_catalog.sql" \
    >/dev/null

psql "${psql_flags[@]}" <<'SQL'
DO $verify_permissions$
BEGIN
    IF has_table_privilege('anon', 'public.file_update_releases', 'SELECT') OR
       has_table_privilege('authenticated', 'public.file_update_releases', 'SELECT') THEN
        RAISE EXCEPTION 'anonymous/authenticated roles can read the file update catalog';
    END IF;
    IF has_table_privilege('service_role', 'public.file_update_releases', 'SELECT') IS NOT TRUE OR
       has_table_privilege('service_role', 'public.file_update_releases', 'INSERT') OR
       has_table_privilege('service_role', 'public.file_update_releases', 'UPDATE') OR
       has_table_privilege('service_role', 'public.file_update_releases', 'DELETE') THEN
        RAISE EXCEPTION 'service_role table privileges do not match read-only catalog access';
    END IF;
    IF has_function_privilege('anon',
       'public.publish_file_update_release_v1(uuid,text,text,text,integer,integer,text,text)', 'EXECUTE') OR
       has_function_privilege('authenticated',
       'public.publish_file_update_release_v1(uuid,text,text,text,integer,integer,text,text)', 'EXECUTE') OR
       has_function_privilege('service_role',
       'public.publish_file_update_release_v1(uuid,text,text,text,integer,integer,text,text)', 'EXECUTE') IS NOT TRUE THEN
        RAISE EXCEPTION 'file update publication RPC grants do not match the service-only contract';
    END IF;
    IF has_function_privilege('anon', 'public.withdraw_file_update_release_v1(uuid,text)', 'EXECUTE') OR
       has_function_privilege('authenticated', 'public.withdraw_file_update_release_v1(uuid,text)', 'EXECUTE') OR
       has_function_privilege('service_role', 'public.withdraw_file_update_release_v1(uuid,text)', 'EXECUTE') IS NOT TRUE THEN
        RAISE EXCEPTION 'file update withdrawal RPC grants do not match the service-only contract';
    END IF;
    IF public.compare_file_update_semver_v1('1.0.0', '1.0.0-rc.1') <> 1 OR
       public.compare_file_update_semver_v1('1.0.0-rc.10', '1.0.0-rc.2') <> 1 OR
       public.compare_file_update_semver_v1('1.0.0+build.1', '1.0.0+build.2') <> 0 THEN
        RAISE EXCEPTION 'SemVer comparison is incorrect';
    END IF;
END;
$verify_permissions$;

SET ROLE service_role;
SELECT public.publish_file_update_release_v1(
    '11111111-1111-4111-8111-111111111111', 'StudentSetup', '1.0.0', 'stable', 1,
    4096, repeat('a', 64), repeat('b', 64)
);
DO $verify_read_policy$
BEGIN
    IF (SELECT count(*) FROM public.file_update_releases) <> 1 THEN
        RAISE EXCEPTION 'service_role cannot read the row through RLS';
    END IF;
END;
$verify_read_policy$;
RESET ROLE;

DO $verify_publication$
DECLARE
    v_result uuid;
    v_rejected boolean;
BEGIN
    v_result := public.publish_file_update_release_v1(
        '11111111-1111-4111-8111-111111111111', 'StudentSetup', '1.0.0', 'stable', 1,
        4096, repeat('a', 64), repeat('b', 64)
    );
    IF v_result <> '11111111-1111-4111-8111-111111111111'::uuid THEN
        RAISE EXCEPTION 'same release retry was not idempotent';
    END IF;
    v_result := public.publish_file_update_release_v1(
        'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'StudentSetup', '1.0.0', 'stable', 1,
        4096, repeat('a', 64), repeat('b', 64)
    );
    IF v_result <> '11111111-1111-4111-8111-111111111111'::uuid THEN
        RAISE EXCEPTION 'matching version retry did not return the existing release';
    END IF;

    v_rejected := false;
    BEGIN
        PERFORM public.publish_file_update_release_v1(
            '22222222-2222-4222-8222-222222222222', 'StudentSetup', '1.0.0-rc.1', 'stable', 2,
            4096, repeat('c', 64), repeat('d', 64)
        );
    EXCEPTION WHEN OTHERS THEN v_rejected := true;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'stable prerelease version was accepted'; END IF;

    v_rejected := false;
    BEGIN
        PERFORM public.publish_file_update_release_v1(
            '33333333-3333-4333-8333-333333333333', 'StudentSetup', '0.9.9', 'stable', 2,
            4096, repeat('e', 64), repeat('f', 64)
        );
    EXCEPTION WHEN OTHERS THEN v_rejected := true;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'same/lower SemVer with a higher sequence was accepted'; END IF;

    v_rejected := false;
    BEGIN
        PERFORM public.publish_file_update_release_v1(
            '44444444-4444-4444-8444-444444444444', 'StudentSetup', '1.0.1', 'stable', 1,
            4096, repeat('1', 64), repeat('2', 64)
        );
    EXCEPTION WHEN OTHERS THEN v_rejected := true;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'non-increasing release sequence was accepted'; END IF;

    PERFORM public.publish_file_update_release_v1(
        '55555555-5555-4555-8555-555555555555', 'StudentSetup', '1.0.1', 'stable', 2,
        8192, repeat('3', 64), repeat('4', 64)
    );
    PERFORM public.publish_file_update_release_v1(
        '66666666-6666-4666-8666-666666666666', 'StudentSetup', '1.1.0-rc.1', 'beta', 1,
        4096, repeat('5', 64), repeat('6', 64)
    );
    PERFORM public.publish_file_update_release_v1(
        '77777777-7777-4777-8777-777777777777', 'StudentSetup', '1.1.0', 'stable', 3,
        12288, repeat('7', 64), repeat('8', 64)
    );
END;
$verify_publication$;

DO $verify_withdrawal$
DECLARE
    v_withdrawn boolean;
    v_latest_version text;
    v_rejected boolean;
BEGIN
    v_withdrawn := public.withdraw_file_update_release_v1(
        '77777777-7777-4777-8777-777777777777', 'test release withdrawn'
    );
    IF NOT v_withdrawn THEN RAISE EXCEPTION 'published release was not withdrawn'; END IF;
    SELECT version INTO v_latest_version FROM public.file_update_releases
     WHERE role = 'StudentSetup' AND channel = 'stable' AND status = 'published'
     ORDER BY release_sequence DESC LIMIT 1;
    IF v_latest_version <> '1.0.1' THEN RAISE EXCEPTION 'withdrawn release remained in published discovery'; END IF;

    v_rejected := false;
    BEGIN
        PERFORM public.publish_file_update_release_v1(
            '88888888-8888-4888-8888-888888888888', 'StudentSetup', '1.2.0', 'stable', 3,
            16384, repeat('9', 64), repeat('a', 64)
        );
    EXCEPTION WHEN OTHERS THEN v_rejected := true;
    END;
    IF NOT v_rejected THEN RAISE EXCEPTION 'withdrawn sequence was incorrectly reusable'; END IF;

    PERFORM public.publish_file_update_release_v1(
        '99999999-9999-4999-8999-999999999999', 'StudentSetup', '1.2.0', 'stable', 4,
        16384, repeat('9', 64), repeat('a', 64)
    );
END;
$verify_withdrawal$;

RESET ROLE;
SQL

printf 'PostgreSQL file-update release catalog checks passed.\n'
