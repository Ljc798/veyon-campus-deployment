BEGIN;

DO $bucket$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM storage.buckets WHERE id = 'application-release-artifacts'
    ) THEN
        INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
        VALUES (
            'application-release-artifacts',
            'application-release-artifacts',
            false,
            536870912,
            ARRAY[
                'application/octet-stream',
                'application/vnd.microsoft.portable-executable',
                'application/x-msdownload'
            ]
        );
    ELSE
        IF EXISTS (
            SELECT 1
              FROM storage.buckets
             WHERE id = 'application-release-artifacts'
               AND (public OR file_size_limit < 536870912)
        ) THEN
            RAISE EXCEPTION 'application-release-artifacts must remain private and allow 512 MiB objects';
        END IF;
    END IF;
END;
$bucket$;

CREATE TABLE public.application_releases (
    release_id uuid PRIMARY KEY,
    role text NOT NULL CHECK (role IN ('TeacherConsole', 'StudentSetup')),
    product text NOT NULL CHECK (
        (role = 'TeacherConsole' AND product = 'VeyonCampus.TeacherConsole') OR
        (role = 'StudentSetup' AND product = 'VeyonCampus.StudentSetup')
    ),
    version varchar(64) NOT NULL CHECK (
        version ~ '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$'
    ),
    architecture text NOT NULL DEFAULT 'win-x64' CHECK (architecture = 'win-x64'),
    file_name text NOT NULL,
    object_key text NOT NULL,
    size_bytes bigint NOT NULL CHECK (size_bytes BETWEEN 1 AND 536870912),
    sha256 char(64) NOT NULL CHECK (sha256 ~ '^[A-F0-9]{64}$'),
    signature_algorithm text NOT NULL DEFAULT 'RSA-PSS-SHA256'
        CHECK (signature_algorithm = 'RSA-PSS-SHA256'),
    signature text NOT NULL CHECK (
        length(signature) BETWEEN 340 AND 8192 AND
        signature ~ '^[A-Za-z0-9+/]+={0,2}$'
    ),
    status text NOT NULL DEFAULT 'published' CHECK (status IN ('published', 'withdrawn')),
    published_at timestamptz NOT NULL DEFAULT now(),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT application_releases_name_matches_role CHECK (
        file_name = CASE role
            WHEN 'TeacherConsole' THEN 'VeyonCampus-Teacher-Setup-' || version || '-win-x64.exe'
            WHEN 'StudentSetup' THEN 'VeyonCampus-Student-Setup-' || version || '-win-x64.exe'
        END
    ),
    CONSTRAINT application_releases_object_key_matches_id CHECK (
        object_key = 'releases/' || role || '/win-x64/' || replace(release_id::text, '-', '') || '.exe'
    ),
    UNIQUE (role, architecture, version)
);

COMMENT ON TABLE public.application_releases IS
    'Developer-published, signed TeacherConsole and StudentSetup release metadata. Artifact bytes live in a separate private storage bucket.';
COMMENT ON COLUMN public.application_releases.signature IS
    'RSA-PSS-SHA256 signature over the canonical manifest fields; the private signing key is never stored in CloudBase.';
COMMENT ON COLUMN public.application_releases.object_key IS
    'Private storage object locator. It is not returned by the anonymous release discovery API.';

CREATE INDEX application_releases_published_role_idx
    ON public.application_releases (role, architecture, published_at DESC)
    WHERE status = 'published';

CREATE TABLE public.campus_daily_teacher_heartbeats (
    day_hkt date NOT NULL,
    campus_identity_digest char(64) NOT NULL CHECK (campus_identity_digest ~ '^[A-F0-9]{64}$'),
    campus_id bigint REFERENCES public.campuses(id) ON DELETE RESTRICT,
    package_id uuid NOT NULL REFERENCES public.deployment_packages(package_id) ON DELETE RESTRICT,
    publisher_digest char(64) NOT NULL CHECK (publisher_digest ~ '^[A-F0-9]{64}$'),
    teacher_version varchar(64) NOT NULL CHECK (
        teacher_version ~ '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$'
    ),
    student_version varchar(64) NOT NULL CHECK (
        student_version ~ '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$'
    ),
    configured_computer_count smallint NOT NULL CHECK (configured_computer_count BETWEEN 0 AND 150),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (day_hkt, campus_identity_digest)
);

COMMENT ON TABLE public.campus_daily_teacher_heartbeats IS
    'One anonymous Teacher heartbeat snapshot per published campus identity and Hong Kong calendar day. Anonymous packages use a server HMAC of normalized campus name and computer prefix; the identity is pseudonymous, not a verified school credential.';
COMMENT ON COLUMN public.campus_daily_teacher_heartbeats.campus_identity_digest IS
    'Server-derived HMAC used to deduplicate registered or anonymous package-backed campus identities without exposing the normalized identity.';
COMMENT ON COLUMN public.campus_daily_teacher_heartbeats.publisher_digest IS
    'Daily HMAC of a random local Teacher publisher identifier; the raw identifier is never stored.';

CREATE INDEX campus_daily_teacher_heartbeats_campus_day_idx
    ON public.campus_daily_teacher_heartbeats (campus_id, day_hkt DESC)
    WHERE campus_id IS NOT NULL;

CREATE FUNCTION public.publish_application_release_v1(
    p_release_id uuid,
    p_role text,
    p_version text,
    p_size_bytes bigint,
    p_sha256 text,
    p_signature text
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
       p_signature !~ '^[A-Za-z0-9+/]+={0,2}$' THEN
        RAISE EXCEPTION 'release signature is invalid';
    END IF;

    v_file_name := 'VeyonCampus-' || v_role_name || '-Setup-' || p_version || '-win-x64.exe';
    v_object_key := 'releases/' || p_role || '/win-x64/' || replace(p_release_id::text, '-', '') || '.exe';
    INSERT INTO public.application_releases (
        release_id, role, product, version, architecture, file_name, object_key,
        size_bytes, sha256, signature_algorithm, signature, status, published_at
    ) VALUES (
        p_release_id, p_role, v_product, p_version, 'win-x64', v_file_name, v_object_key,
        p_size_bytes, upper(p_sha256), 'RSA-PSS-SHA256', p_signature, 'published', now()
    );
    RETURN p_release_id;
END;
$function$;

CREATE FUNCTION public.record_campus_teacher_heartbeat_v1(
    p_day_hkt date,
    p_publisher_digest text,
    p_package_id uuid,
    p_campus_identity_digest text,
    p_teacher_version text,
    p_student_version text,
    p_configured_computer_count integer
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_campus_id bigint;
BEGIN
    IF p_day_hkt IS DISTINCT FROM (timezone('Asia/Hong_Kong', statement_timestamp()))::date THEN
        RAISE EXCEPTION 'heartbeat date must be the current Asia/Hong_Kong date';
    END IF;
    IF p_publisher_digest IS NULL OR p_publisher_digest !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'publisher digest is invalid';
    END IF;
    IF p_campus_identity_digest IS NULL OR p_campus_identity_digest !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'campus identity digest is invalid';
    END IF;
    IF p_teacher_version IS NULL OR length(p_teacher_version) > 64 OR
       p_teacher_version !~ '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$' OR
       p_student_version IS NULL OR length(p_student_version) > 64 OR
       p_student_version !~ '^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$' THEN
        RAISE EXCEPTION 'heartbeat version is invalid';
    END IF;
    IF p_configured_computer_count NOT BETWEEN 0 AND 150 THEN
        RAISE EXCEPTION 'configured computer count is invalid';
    END IF;
    SELECT package.campus_id
      INTO v_campus_id
      FROM public.deployment_packages AS package
      LEFT JOIN public.campuses AS campus ON campus.id = package.campus_id
     WHERE package.package_id = p_package_id
       AND package.status = 'published'
       AND (package.campus_id IS NULL OR campus.status = 'active');
    IF NOT FOUND THEN
        RAISE EXCEPTION 'published package is not mapped to an active campus';
    END IF;

    INSERT INTO public.campus_daily_teacher_heartbeats (
        day_hkt, campus_identity_digest, campus_id, package_id, publisher_digest,
        teacher_version, student_version, configured_computer_count, updated_at
    ) VALUES (
        p_day_hkt, upper(p_campus_identity_digest), v_campus_id, p_package_id, upper(p_publisher_digest),
        p_teacher_version, p_student_version, p_configured_computer_count, now()
    )
    ON CONFLICT (day_hkt, campus_identity_digest) DO UPDATE SET
        campus_id = EXCLUDED.campus_id,
        package_id = EXCLUDED.package_id,
        publisher_digest = EXCLUDED.publisher_digest,
        teacher_version = EXCLUDED.teacher_version,
        student_version = EXCLUDED.student_version,
        configured_computer_count = EXCLUDED.configured_computer_count,
        updated_at = now();
END;
$function$;

REVOKE ALL ON TABLE public.application_releases FROM PUBLIC, anon, authenticated, service_role;
GRANT SELECT ON TABLE public.application_releases TO service_role;
ALTER TABLE public.application_releases ENABLE ROW LEVEL SECURITY;
CREATE POLICY application_releases_service_read
    ON public.application_releases FOR SELECT TO service_role USING (true);

REVOKE ALL ON FUNCTION public.publish_application_release_v1(uuid, text, text, bigint, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_application_release_v1(uuid, text, text, bigint, text, text)
    TO service_role;

REVOKE ALL ON TABLE public.campus_daily_teacher_heartbeats FROM PUBLIC, anon, authenticated, service_role;
GRANT ALL ON TABLE public.campus_daily_teacher_heartbeats TO service_role;
GRANT SELECT ON TABLE public.campus_daily_teacher_heartbeats TO authenticated;
ALTER TABLE public.campus_daily_teacher_heartbeats ENABLE ROW LEVEL SECURITY;
CREATE POLICY campus_daily_teacher_heartbeats_service_write
    ON public.campus_daily_teacher_heartbeats FOR ALL TO service_role USING (true) WITH CHECK (true);
CREATE POLICY campus_daily_teacher_heartbeats_admin_read
    ON public.campus_daily_teacher_heartbeats FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_profiles AS profile
            WHERE profile.user_id = (SELECT auth.uid())
              AND profile.role IN ('owner', 'admin', 'editor', 'viewer')
        )
    );

REVOKE ALL ON FUNCTION public.record_campus_teacher_heartbeat_v1(date, text, uuid, text, text, text, integer)
    FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.record_campus_teacher_heartbeat_v1(date, text, uuid, text, text, text, integer)
    TO service_role;

COMMIT;
