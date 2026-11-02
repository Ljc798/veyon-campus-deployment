BEGIN;

ALTER TABLE public.application_releases
    ADD COLUMN student_agent_version varchar(64),
    ADD COLUMN student_agent_target_signature text,
    ADD CONSTRAINT application_releases_student_agent_target_shape CHECK (
        (student_agent_version IS NULL AND student_agent_target_signature IS NULL) OR
        (role = 'StudentSetup' AND
         student_agent_version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$' AND
         length(student_agent_target_signature) BETWEEN 340 AND 8192 AND
         student_agent_target_signature ~ '^[A-Za-z0-9+/]+={0,2}$')
    );

CREATE FUNCTION public.publish_application_release_v4(
    p_release_id uuid,
    p_role text,
    p_version text,
    p_size_bytes bigint,
    p_sha256 text,
    p_signature text,
    p_legacy_signature text,
    p_legacy_system_policy_signature text,
    p_application_policy_capability smallint,
    p_student_system_policy_capability smallint,
    p_student_agent_version text,
    p_student_agent_target_signature text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_release_id uuid;
BEGIN
    IF p_role = 'StudentSetup' THEN
        IF p_student_agent_version IS NULL OR length(p_student_agent_version) > 64 OR
           p_student_agent_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$' OR
           p_student_agent_target_signature IS NULL OR
           length(p_student_agent_target_signature) NOT BETWEEN 340 AND 8192 OR
           p_student_agent_target_signature !~ '^[A-Za-z0-9+/]+={0,2}$' THEN
            RAISE EXCEPTION 'Student Agent target version declaration is invalid';
        END IF;
    ELSIF p_role = 'TeacherConsole' THEN
        IF p_student_agent_version IS NOT NULL OR p_student_agent_target_signature IS NOT NULL THEN
            RAISE EXCEPTION 'TeacherConsole release cannot declare a Student Agent target';
        END IF;
    ELSE
        RAISE EXCEPTION 'release role is invalid';
    END IF;

    v_release_id := public.publish_application_release_v3(
        p_release_id, p_role, p_version, p_size_bytes, p_sha256, p_signature,
        p_legacy_signature, p_legacy_system_policy_signature,
        p_application_policy_capability, p_student_system_policy_capability
    );
    UPDATE public.application_releases
       SET student_agent_version = p_student_agent_version,
           student_agent_target_signature = p_student_agent_target_signature
     WHERE release_id = v_release_id;
    RETURN v_release_id;
END;
$function$;

-- Existing immutable StudentSetup releases can receive this additive, separately
-- signed descriptor only when the exact Setup version and installer digest match.
CREATE FUNCTION public.bind_application_release_agent_target_v1(
    p_role text,
    p_version text,
    p_sha256 text,
    p_student_agent_version text,
    p_student_agent_target_signature text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_release_id uuid;
    v_agent_version text;
    v_agent_signature text;
BEGIN
    IF p_role <> 'StudentSetup' OR p_version IS NULL OR length(p_version) > 64 OR
       p_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$' OR
       p_sha256 IS NULL OR upper(p_sha256) !~ '^[A-F0-9]{64}$' OR
       p_student_agent_version IS NULL OR length(p_student_agent_version) > 64 OR
       p_student_agent_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$' OR
       p_student_agent_target_signature IS NULL OR
       length(p_student_agent_target_signature) NOT BETWEEN 340 AND 8192 OR
       p_student_agent_target_signature !~ '^[A-Za-z0-9+/]+={0,2}$' THEN
        RAISE EXCEPTION 'Student Agent target binding is invalid';
    END IF;

    SELECT release_id, student_agent_version, student_agent_target_signature
      INTO v_release_id, v_agent_version, v_agent_signature
      FROM public.application_releases
     WHERE role = 'StudentSetup' AND version = p_version AND upper(sha256) = upper(p_sha256) AND status = 'published'
     FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'matching published StudentSetup release was not found';
    END IF;
    IF (v_agent_version IS NOT NULL AND v_agent_version <> p_student_agent_version) OR
       (v_agent_signature IS NOT NULL AND v_agent_signature <> p_student_agent_target_signature) THEN
        RAISE EXCEPTION 'Student Agent target binding conflicts with the existing descriptor';
    END IF;

    UPDATE public.application_releases
       SET student_agent_version = p_student_agent_version,
           student_agent_target_signature = p_student_agent_target_signature
     WHERE release_id = v_release_id;
    RETURN v_release_id;
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_application_release_v4(
    uuid, text, text, bigint, text, text, text, text, smallint, smallint, text, text
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_application_release_v4(
    uuid, text, text, bigint, text, text, text, text, smallint, smallint, text, text
) TO service_role;
REVOKE ALL ON FUNCTION public.bind_application_release_agent_target_v1(
    text, text, text, text, text
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.bind_application_release_agent_target_v1(
    text, text, text, text, text
) TO service_role;

COMMENT ON COLUMN public.application_releases.student_agent_version IS
    'Student Agent product version bundled by the exact signed StudentSetup installer; exposed only as a separately signed teacher-side descriptor.';
COMMENT ON COLUMN public.application_releases.student_agent_target_signature IS
    'Developer Release RSA-PSS signature over the Agent target descriptor bound to Setup version and installer SHA-256.';

COMMIT;
