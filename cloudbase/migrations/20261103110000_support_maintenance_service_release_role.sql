BEGIN;

-- MaintenanceService has its own signed version stream. It is deliberately excluded
-- from component file-update releases and from the StudentSetup Agent-target sidecar.
ALTER TABLE public.application_releases
    DROP CONSTRAINT IF EXISTS application_releases_role_check,
    DROP CONSTRAINT IF EXISTS application_releases_product_check,
    DROP CONSTRAINT IF EXISTS application_releases_name_matches_role_check,
    ADD COLUMN maintenance_protocol_version smallint;

ALTER TABLE public.application_releases
    ADD CONSTRAINT application_releases_role_check
        CHECK (role IN ('TeacherConsole', 'StudentSetup', 'MaintenanceService')),
    ADD CONSTRAINT application_releases_product_check CHECK (
        (role = 'TeacherConsole' AND product = 'VeyonCampus.TeacherConsole') OR
        (role = 'StudentSetup' AND product = 'VeyonCampus.StudentSetup') OR
        (role = 'MaintenanceService' AND product = 'VeyonCampus.MaintenanceService')
    ),
    ADD CONSTRAINT application_releases_name_matches_role_check CHECK (
        file_name = CASE role
            WHEN 'TeacherConsole' THEN 'VeyonCampus-Teacher-Setup-' || version || '-win-x64.exe'
            WHEN 'StudentSetup' THEN 'VeyonCampus-Student-Setup-' || version || '-win-x64.exe'
            WHEN 'MaintenanceService' THEN 'VeyonCampus-Maintenance-Setup-' || version || '-win-x64.exe'
        END
    );

ALTER TABLE public.application_releases
    DROP CONSTRAINT IF EXISTS application_releases_manifest_schema_version_check,
    DROP CONSTRAINT IF EXISTS application_releases_manifest_capability_shape,
    ADD CONSTRAINT application_releases_manifest_schema_version_check
        CHECK (manifest_schema_version IN (1, 2, 3, 5)),
    ADD CONSTRAINT application_releases_manifest_capability_shape CHECK (
        (manifest_schema_version = 1 AND student_system_policy_capability = 0 AND
            application_policy_capability = 0 AND legacy_signature IS NULL AND
            legacy_system_policy_signature IS NULL) OR
        (manifest_schema_version = 2 AND role <> 'MaintenanceService' AND
            student_system_policy_capability BETWEEN 1 AND 32767 AND application_policy_capability = 0 AND
            legacy_signature IS NOT NULL AND length(legacy_signature) BETWEEN 340 AND 8192 AND
            legacy_signature ~ '^[A-Za-z0-9+/]+={0,2}$' AND legacy_system_policy_signature IS NULL) OR
        (manifest_schema_version = 3 AND role <> 'MaintenanceService' AND
            student_system_policy_capability BETWEEN 1 AND 32767 AND
            application_policy_capability BETWEEN 1 AND 32767 AND legacy_signature IS NOT NULL AND
            length(legacy_signature) BETWEEN 340 AND 8192 AND legacy_signature ~ '^[A-Za-z0-9+/]+={0,2}$' AND
            legacy_system_policy_signature IS NOT NULL AND length(legacy_system_policy_signature) BETWEEN 340 AND 8192 AND
            legacy_system_policy_signature ~ '^[A-Za-z0-9+/]+={0,2}$') OR
        (manifest_schema_version = 5 AND role = 'MaintenanceService' AND
            student_system_policy_capability = 0 AND application_policy_capability = 0 AND
            legacy_signature IS NULL AND legacy_system_policy_signature IS NULL)
    ),
    ADD CONSTRAINT application_releases_maintenance_protocol_shape CHECK (
        (role = 'MaintenanceService' AND manifest_schema_version = 5 AND
            maintenance_protocol_version BETWEEN 1 AND 32767 AND
            student_agent_version IS NULL AND student_agent_target_signature IS NULL) OR
        (role <> 'MaintenanceService' AND maintenance_protocol_version IS NULL AND manifest_schema_version IN (1, 2, 3))
    );

CREATE FUNCTION public.publish_maintenance_service_release_v1(
    p_release_id uuid,
    p_version text,
    p_size_bytes bigint,
    p_sha256 text,
    p_signature text,
    p_maintenance_protocol_version smallint
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_file_name text;
    v_object_key text;
BEGIN
    IF p_release_id IS NULL OR p_release_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
        RAISE EXCEPTION 'release id is invalid';
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
       p_signature !~ '^[A-Za-z0-9+/]+={0,2}$' THEN
        RAISE EXCEPTION 'release signature is invalid';
    END IF;
    IF p_maintenance_protocol_version NOT BETWEEN 1 AND 32767 THEN
        RAISE EXCEPTION 'Maintenance protocol version is invalid';
    END IF;

    v_file_name := 'VeyonCampus-Maintenance-Setup-' || p_version || '-win-x64.exe';
    v_object_key := 'releases/MaintenanceService/win-x64/' || replace(p_release_id::text, '-', '') || '.exe';
    INSERT INTO public.application_releases (
        release_id, role, product, version, architecture, file_name, object_key,
        size_bytes, sha256, signature_algorithm, signature, status, published_at,
        manifest_schema_version, student_system_policy_capability, application_policy_capability,
        legacy_signature, legacy_system_policy_signature, maintenance_protocol_version
    ) VALUES (
        p_release_id, 'MaintenanceService', 'VeyonCampus.MaintenanceService', p_version, 'win-x64',
        v_file_name, v_object_key, p_size_bytes, upper(p_sha256), 'RSA-PSS-SHA256', p_signature,
        'published', now(), 5, 0, 0, NULL, NULL, p_maintenance_protocol_version
    );
    RETURN p_release_id;
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_maintenance_service_release_v1(uuid, text, bigint, text, text, smallint)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_maintenance_service_release_v1(uuid, text, bigint, text, text, smallint)
    TO service_role;

COMMENT ON COLUMN public.application_releases.maintenance_protocol_version IS
    'Protocol version implemented by a signed MaintenanceService release; required only for schema-5 MaintenanceService artifacts.';

COMMIT;
