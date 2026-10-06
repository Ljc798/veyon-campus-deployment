BEGIN;

-- v4 adds the separately hashed application-policy trust key to the existing
-- fixed campus archive. v3 package bytes, rows and object keys stay untouched.
ALTER TABLE public.deployment_packages
    DROP CONSTRAINT deployment_packages_schema_version_check,
    ADD CONSTRAINT deployment_packages_schema_version_check CHECK (schema_version IN (3, 4));

ALTER TABLE public.deployment_package_artifacts
    DROP CONSTRAINT deployment_package_artifacts_storage_key_rule,
    ADD CONSTRAINT deployment_package_artifacts_storage_key_rule
        CHECK (storage_key ~ '^deployment-packages/v[34]/[^/\\]+\.zip$');

CREATE OR REPLACE FUNCTION public.set_deployment_package_campus_storage_key()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
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

    IF NOT FOUND OR v_schema_version NOT IN (3, 4) THEN
        RAISE EXCEPTION 'supported deployment package must exist before its storage artifact';
    END IF;

    NEW.storage_key := 'deployment-packages/v' || v_schema_version::text || '/' || v_file_name;
    RETURN NEW;
END;
$function$;

DROP FUNCTION public.publish_deployment_package_public(
    uuid, text, text, text, integer, text, text, text
);

CREATE FUNCTION public.publish_deployment_package_public(
    p_package_id uuid,
    p_schema_version integer,
    p_campus_name text,
    p_publisher_name text,
    p_computer_prefix text,
    p_artifact_size_bytes integer,
    p_artifact_sha256 text,
    p_publisher_identity_fingerprint text,
    p_phone_fingerprint text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF p_schema_version NOT IN (3, 4) THEN
        RAISE EXCEPTION 'package schema version is unsupported';
    END IF;
    IF p_publisher_name IS NULL
       OR length(btrim(p_publisher_name)) NOT BETWEEN 1 AND 100
       OR p_publisher_name <> btrim(p_publisher_name)
       OR p_publisher_name ~ '[[:cntrl:]]' THEN
        RAISE EXCEPTION 'publisher name is invalid';
    END IF;
    IF p_artifact_size_bytes NOT BETWEEN 1 AND 65536 THEN
        RAISE EXCEPTION 'package exceeds the 64 KiB limit';
    END IF;
    IF p_artifact_sha256 IS NULL OR upper(p_artifact_sha256) !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'package digest is invalid';
    END IF;
    IF p_publisher_identity_fingerprint IS NULL
       OR upper(p_publisher_identity_fingerprint) !~ '^[A-F0-9]{64}$'
       OR p_phone_fingerprint IS NULL
       OR upper(p_phone_fingerprint) !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'teacher name and phone suffix are required';
    END IF;

    INSERT INTO public.deployment_packages (
        package_id, campus_id, campus_name, computer_prefix, schema_version,
        artifact_size_bytes, artifact_sha256, publisher_name,
        publisher_identity_fingerprint, publisher_phone_fingerprint, created_by_user_id
    ) VALUES (
        p_package_id, NULL, p_campus_name, p_computer_prefix, p_schema_version,
        p_artifact_size_bytes, upper(p_artifact_sha256), p_publisher_name,
        upper(p_publisher_identity_fingerprint), upper(p_phone_fingerprint), 'public'
    );

    INSERT INTO public.deployment_package_artifacts (package_id) VALUES (p_package_id);
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_deployment_package_public(
    uuid, integer, text, text, text, integer, text, text, text
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_deployment_package_public(
    uuid, integer, text, text, text, integer, text, text, text
) TO service_role;

COMMENT ON COLUMN public.deployment_packages.schema_version IS
    'Validated immutable campus package protocol version. v4 includes the application-policy trust key.';
COMMENT ON TABLE public.deployment_package_artifacts IS
    'Private v3/v4 object locator. The server generates the version prefix and campus-named immutable filename.';

COMMIT;
