#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${PGDATABASE:-}" ]]; then
    printf 'Set PGDATABASE to a disposable, empty PostgreSQL database before running this check.\n' >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
psql_flags=(-X -v ON_ERROR_STOP=1)

psql "${psql_flags[@]}" <<'SQL'
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') OR
       NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') OR
       NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
        RAISE EXCEPTION 'CloudBase-compatible anon/authenticated/service_role roles must already exist';
    END IF;
END;
$roles$;

CREATE FUNCTION public.deployment_package_campus_file_stem(p_campus_name text)
RETURNS text LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog, public
AS $function$
    SELECT COALESCE(NULLIF(btrim(regexp_replace(p_campus_name, '[[:cntrl:]<>:"/\\|?*]', '-', 'g'), ' .'), ''), 'campus');
$function$;

CREATE TABLE public.deployment_packages (
    package_id uuid PRIMARY KEY,
    campus_id bigint,
    campus_name text NOT NULL,
    computer_prefix text NOT NULL,
    schema_version smallint NOT NULL CONSTRAINT deployment_packages_schema_version_check CHECK (schema_version IN (3, 4, 5)),
    artifact_file_name text GENERATED ALWAYS AS (
        public.deployment_package_campus_file_stem(campus_name) || '-' || replace(package_id::text, '-', '') || '.zip'
    ) STORED,
    artifact_size_bytes integer NOT NULL,
    artifact_sha256 text NOT NULL,
    publisher_name text NOT NULL,
    publisher_identity_fingerprint text NOT NULL,
    publisher_phone_fingerprint text NOT NULL,
    created_by_user_id text NOT NULL
);

CREATE TABLE public.deployment_package_artifacts (
    package_id uuid PRIMARY KEY REFERENCES public.deployment_packages(package_id),
    storage_key text NOT NULL CONSTRAINT deployment_package_artifacts_storage_key_rule
        CHECK (storage_key ~ '^deployment-packages/v[345]/[^/\\]+\.zip$')
);

CREATE FUNCTION public.set_deployment_package_campus_storage_key()
RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
DECLARE
    v_file_name text;
    v_schema_version smallint;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF ROW(NEW.package_id, NEW.storage_key) IS DISTINCT FROM ROW(OLD.package_id, OLD.storage_key) THEN
            RAISE EXCEPTION 'published package storage keys are immutable';
        END IF;
        RETURN NEW;
    END IF;
    SELECT package.artifact_file_name, package.schema_version
      INTO v_file_name, v_schema_version
      FROM public.deployment_packages AS package
     WHERE package.package_id = NEW.package_id;
    IF NOT FOUND OR v_schema_version NOT IN (3, 4, 5) THEN
        RAISE EXCEPTION 'supported deployment package must exist before its storage artifact';
    END IF;
    NEW.storage_key := 'deployment-packages/v' || v_schema_version::text || '/' || v_file_name;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER deployment_package_artifacts_set_campus_storage_key
BEFORE INSERT OR UPDATE ON public.deployment_package_artifacts
FOR EACH ROW EXECUTE FUNCTION public.set_deployment_package_campus_storage_key();

CREATE FUNCTION public.publish_deployment_package_public(
    uuid, integer, text, text, text, integer, text, text, text
) RETURNS void LANGUAGE plpgsql AS $function$ BEGIN RETURN; END; $function$;

INSERT INTO public.deployment_packages (
    package_id, campus_id, campus_name, computer_prefix, schema_version,
    artifact_size_bytes, artifact_sha256, publisher_name,
    publisher_identity_fingerprint, publisher_phone_fingerprint, created_by_user_id
) VALUES (
    '11111111-1111-4111-8111-111111111111', NULL, 'Legacy v5 campus', 'V5-', 5,
    128, repeat('A', 64), 'fixture', repeat('B', 64), repeat('C', 64), 'public'
);
INSERT INTO public.deployment_package_artifacts (package_id)
VALUES ('11111111-1111-4111-8111-111111111111');
SQL

psql "${psql_flags[@]}" --file "$repo_root/cloudbase/migrations/20261008100000_support_package_setup_recommendations.sql" >/dev/null

psql "${psql_flags[@]}" <<'SQL'
SET ROLE service_role;
SELECT public.publish_deployment_package_public(
    '22222222-2222-4222-8222-222222222222', 6, 'V6 campus', 'fixture', 'PC-',
    128, repeat('D', 64), repeat('E', 64), repeat('F', 64)
);
RESET ROLE;

DO $verify$
DECLARE
    v5_key text;
    v6_key text;
    function_oid regprocedure := 'public.publish_deployment_package_public(uuid,integer,text,text,text,integer,text,text,text)'::regprocedure;
    rejected boolean := false;
BEGIN
    SELECT storage_key INTO v5_key FROM public.deployment_package_artifacts
     WHERE package_id = '11111111-1111-4111-8111-111111111111';
    SELECT storage_key INTO v6_key FROM public.deployment_package_artifacts
     WHERE package_id = '22222222-2222-4222-8222-222222222222';
    IF v5_key <> 'deployment-packages/v5/Legacy v5 campus-11111111111141118111111111111111.zip' THEN
        RAISE EXCEPTION 'v5 object key changed: %', v5_key;
    END IF;
    IF v6_key <> 'deployment-packages/v6/V6 campus-22222222222242228222222222222222.zip' THEN
        RAISE EXCEPTION 'v6 object key was not generated: %', v6_key;
    END IF;
    IF has_function_privilege('anon', function_oid, 'EXECUTE') OR
       has_function_privilege('authenticated', function_oid, 'EXECUTE') OR
       NOT has_function_privilege('service_role', function_oid, 'EXECUTE') THEN
        RAISE EXCEPTION 'package publish RPC grants are incorrect';
    END IF;
    BEGIN
        PERFORM public.publish_deployment_package_public(
            '33333333-3333-4333-8333-333333333333', 7, 'Unsupported', 'fixture', 'PC-',
            128, repeat('1', 64), repeat('2', 64), repeat('3', 64)
        );
    EXCEPTION WHEN raise_exception THEN
        rejected := true;
    END;
    IF NOT rejected THEN RAISE EXCEPTION 'unsupported schema version was published'; END IF;
    BEGIN
        UPDATE public.deployment_package_artifacts
           SET storage_key = 'deployment-packages/v6/changed.zip'
         WHERE package_id = '22222222-2222-4222-8222-222222222222';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM <> 'published package storage keys are immutable' THEN RAISE; END IF;
    END;
    SELECT storage_key INTO v6_key FROM public.deployment_package_artifacts
     WHERE package_id = '22222222-2222-4222-8222-222222222222';
    IF v6_key <> 'deployment-packages/v6/V6 campus-22222222222242228222222222222222.zip' THEN
        RAISE EXCEPTION 'v6 object key was changed';
    END IF;
END;
$verify$;
SQL

printf 'PostgreSQL v6 package migration checks passed.\n'
