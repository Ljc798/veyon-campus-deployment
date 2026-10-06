BEGIN;

-- Signed release protocol v2 adds policy capabilities. Keep the original
-- schema-1 signature alongside v2 so installed clients using /v1 can continue
-- to verify and receive the same artifact during a staged client rollout.
ALTER TABLE public.application_releases
    ADD COLUMN manifest_schema_version smallint NOT NULL DEFAULT 1
        CHECK (manifest_schema_version IN (1, 2)),
    ADD COLUMN student_system_policy_capability smallint NOT NULL DEFAULT 0
        CHECK (student_system_policy_capability BETWEEN 0 AND 32767),
    ADD COLUMN legacy_signature text,
    ADD CONSTRAINT application_releases_manifest_capability_shape CHECK (
        (manifest_schema_version = 1 AND student_system_policy_capability = 0 AND legacy_signature IS NULL) OR
        (manifest_schema_version = 2 AND student_system_policy_capability >= 1 AND legacy_signature IS NOT NULL
            AND length(legacy_signature) BETWEEN 340 AND 8192
            AND legacy_signature ~ '^[A-Za-z0-9+/]+={0,2}$')
    );

CREATE FUNCTION public.publish_application_release_v2(
    p_release_id uuid,
    p_role text,
    p_version text,
    p_size_bytes bigint,
    p_sha256 text,
    p_signature text,
    p_legacy_signature text,
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
       p_legacy_signature !~ '^[A-Za-z0-9+/]+={0,2}$' THEN
        RAISE EXCEPTION 'release signature is invalid';
    END IF;
    IF p_student_system_policy_capability < 1 THEN
        RAISE EXCEPTION 'student system policy capability is required';
    END IF;

    v_file_name := 'VeyonCampus-' || v_role_name || '-Setup-' || p_version || '-win-x64.exe';
    v_object_key := 'releases/' || p_role || '/win-x64/' || replace(p_release_id::text, '-', '') || '.exe';
    INSERT INTO public.application_releases (
        release_id, role, product, version, architecture, file_name, object_key,
        size_bytes, sha256, signature_algorithm, signature, status, published_at,
        manifest_schema_version, student_system_policy_capability, legacy_signature
    ) VALUES (
        p_release_id, p_role, v_product, p_version, 'win-x64', v_file_name, v_object_key,
        p_size_bytes, upper(p_sha256), 'RSA-PSS-SHA256', p_signature, 'published', now(),
        2, p_student_system_policy_capability, p_legacy_signature
    );
    RETURN p_release_id;
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_application_release_v2(
    uuid, text, text, bigint, text, text, text, smallint
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_application_release_v2(
    uuid, text, text, bigint, text, text, text, smallint
) TO service_role;

COMMENT ON COLUMN public.application_releases.manifest_schema_version IS
    'Signed Developer Release manifest schema. Schema 2 declares the Student SYSTEM Policy capability.';
COMMENT ON COLUMN public.application_releases.legacy_signature IS
    'Schema-1 signature over the same artifact metadata for installed clients using the stable /v1 release API.';
COMMENT ON COLUMN public.application_releases.student_system_policy_capability IS
    'Minimum Student SYSTEM Policy runtime capability declared by the signed schema-2 manifest; zero means absent.';

COMMIT;
