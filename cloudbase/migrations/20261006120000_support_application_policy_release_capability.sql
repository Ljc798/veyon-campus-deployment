BEGIN;

-- Release schema 3 declares both classroom application-policy and long-lived
-- student system-policy compatibility. Keep schema-1 and schema-2 signatures
-- so installed clients on /v1 and /v2 can continue to verify the same artifact.
ALTER TABLE public.application_releases
    ADD COLUMN application_policy_capability smallint NOT NULL DEFAULT 0
        CHECK (application_policy_capability BETWEEN 0 AND 32767),
    ADD COLUMN legacy_system_policy_signature text;

ALTER TABLE public.application_releases
    DROP CONSTRAINT IF EXISTS application_releases_manifest_schema_version_check,
    DROP CONSTRAINT IF EXISTS application_releases_manifest_capability_shape,
    ADD CONSTRAINT application_releases_manifest_schema_version_check
        CHECK (manifest_schema_version IN (1, 2, 3)),
    ADD CONSTRAINT application_releases_manifest_capability_shape CHECK (
        (manifest_schema_version = 1 AND student_system_policy_capability = 0 AND
            application_policy_capability = 0 AND legacy_signature IS NULL AND
            legacy_system_policy_signature IS NULL) OR
        (manifest_schema_version = 2 AND student_system_policy_capability BETWEEN 1 AND 32767 AND
            application_policy_capability = 0 AND legacy_signature IS NOT NULL AND
            length(legacy_signature) BETWEEN 340 AND 8192 AND
            legacy_signature ~ '^[A-Za-z0-9+/]+={0,2}$' AND legacy_system_policy_signature IS NULL) OR
        (manifest_schema_version = 3 AND student_system_policy_capability BETWEEN 1 AND 32767 AND
            application_policy_capability BETWEEN 1 AND 32767 AND legacy_signature IS NOT NULL AND
            length(legacy_signature) BETWEEN 340 AND 8192 AND
            legacy_signature ~ '^[A-Za-z0-9+/]+={0,2}$' AND legacy_system_policy_signature IS NOT NULL AND
            length(legacy_system_policy_signature) BETWEEN 340 AND 8192 AND
            legacy_system_policy_signature ~ '^[A-Za-z0-9+/]+={0,2}$')
    );

CREATE FUNCTION public.publish_application_release_v3(
    p_release_id uuid,
    p_role text,
    p_version text,
    p_size_bytes bigint,
    p_sha256 text,
    p_signature text,
    p_legacy_signature text,
    p_legacy_system_policy_signature text,
    p_application_policy_capability smallint,
    p_student_system_policy_capability smallint
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_product text;
    v_role_name text;
    v_file_name text;
    v_object_key text;
BEGIN
    IF p_release_id IS NULL OR p_release_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'release id is invalid';
    END IF;
    IF p_role = 'TeacherConsole' THEN
        v_product := 'VeyonCampus.TeacherConsole';
        v_role_name := 'Teacher';
    ELSIF p_role = 'StudentSetup' THEN
        v_product := 'VeyonCampus.StudentSetup';
        v_role_name := 'Student';
    ELSE
        RAISE EXCEPTION 'release role is invalid';
    END IF;
    IF p_version IS NULL OR length(p_version) > 64 OR
       p_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$' THEN
        RAISE EXCEPTION 'release version is invalid';
    END IF;
    IF p_size_bytes NOT BETWEEN 1 AND 536870912 THEN
        RAISE EXCEPTION 'release artifact size is invalid';
    END IF;
    IF p_sha256 IS NULL OR upper(p_sha256) !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'release SHA-256 is invalid';
    END IF;
    IF p_signature IS NULL OR length(p_signature) NOT BETWEEN 340 AND 8192 OR
       p_signature !~ '^[A-Za-z0-9+/]+={0,2}$' OR
       p_legacy_signature IS NULL OR length(p_legacy_signature) NOT BETWEEN 340 AND 8192 OR
       p_legacy_signature !~ '^[A-Za-z0-9+/]+={0,2}$' OR
       p_legacy_system_policy_signature IS NULL OR
       length(p_legacy_system_policy_signature) NOT BETWEEN 340 AND 8192 OR
       p_legacy_system_policy_signature !~ '^[A-Za-z0-9+/]+={0,2}$' THEN
        RAISE EXCEPTION 'release signature is invalid';
    END IF;
    IF p_application_policy_capability NOT BETWEEN 1 AND 32767 OR
       p_student_system_policy_capability NOT BETWEEN 1 AND 32767 THEN
        RAISE EXCEPTION 'release policy capabilities are invalid';
    END IF;

    v_file_name := 'VeyonCampus-' || v_role_name || '-Setup-' || p_version || '-win-x64.exe';
    v_object_key := 'releases/' || p_role || '/win-x64/' || replace(p_release_id::text, '-', '') || '.exe';
    INSERT INTO public.application_releases (
        release_id, role, product, version, architecture, file_name, object_key,
        size_bytes, sha256, signature_algorithm, signature, status, published_at,
        manifest_schema_version, student_system_policy_capability, application_policy_capability,
        legacy_signature, legacy_system_policy_signature
    ) VALUES (
        p_release_id, p_role, v_product, p_version, 'win-x64', v_file_name, v_object_key,
        p_size_bytes, upper(p_sha256), 'RSA-PSS-SHA256', p_signature, 'published', now(),
        3, p_student_system_policy_capability, p_application_policy_capability,
        p_legacy_signature, p_legacy_system_policy_signature
    );
    RETURN p_release_id;
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_application_release_v3(
    uuid, text, text, bigint, text, text, text, text, smallint, smallint
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_application_release_v3(
    uuid, text, text, bigint, text, text, text, text, smallint, smallint
) TO service_role;

COMMENT ON COLUMN public.application_releases.application_policy_capability IS
    'Minimum classroom application-policy runtime capability in the signed schema-3 release manifest; zero means absent.';
COMMENT ON COLUMN public.application_releases.legacy_system_policy_signature IS
    'Schema-2 signature for installed clients using the stable /v2 release API.';

COMMIT;
