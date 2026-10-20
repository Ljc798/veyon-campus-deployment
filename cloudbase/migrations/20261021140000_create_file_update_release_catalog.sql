BEGIN;

CREATE TABLE public.file_update_releases (
    release_id uuid PRIMARY KEY,
    role text NOT NULL CHECK (role IN ('TeacherConsole', 'StudentSetup')),
    product text NOT NULL CHECK (
        (role = 'TeacherConsole' AND product = 'VeyonCampus.TeacherConsole') OR
        (role = 'StudentSetup' AND product = 'VeyonCampus.StudentSetup')
    ),
    version varchar(64) NOT NULL CHECK (
        version ~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
    ),
    architecture text NOT NULL DEFAULT 'win-x64' CHECK (architecture = 'win-x64'),
    channel text NOT NULL CHECK (channel IN ('stable', 'beta')),
    release_sequence integer NOT NULL CHECK (release_sequence BETWEEN 1 AND 2147483647),
    manifest_size_bytes integer NOT NULL CHECK (manifest_size_bytes BETWEEN 1 AND 16777216),
    manifest_sha256 char(64) NOT NULL CHECK (manifest_sha256 ~ '^[a-f0-9]{64}$'),
    manifest_key_id char(64) NOT NULL CHECK (manifest_key_id ~ '^[a-f0-9]{64}$'),
    status text NOT NULL DEFAULT 'published' CHECK (status IN ('published', 'withdrawn')),
    published_at timestamptz NOT NULL DEFAULT now(),
    withdrawn_at timestamptz,
    withdrawn_reason text,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT file_update_releases_withdrawal_shape CHECK (
        (status = 'published' AND withdrawn_at IS NULL AND withdrawn_reason IS NULL) OR
        (status = 'withdrawn' AND withdrawn_at IS NOT NULL AND withdrawn_reason IS NOT NULL AND
            length(withdrawn_reason) BETWEEN 1 AND 500)
    ),
    CONSTRAINT file_update_stable_version_has_no_prerelease CHECK (
        channel <> 'stable' OR position('-' IN version) = 0
    ),
    UNIQUE (role, architecture, channel, version),
    UNIQUE (role, architecture, channel, release_sequence)
);

COMMENT ON TABLE public.file_update_releases IS
    'Service-only discovery index for externally hosted, developer-signed complete file manifests. Objects remain in immutable release assets; this row never authorizes installation.';
COMMENT ON COLUMN public.file_update_releases.manifest_sha256 IS
    'Lowercase SHA-256 of the exact canonical UTF-8 manifest.json release asset.';
COMMENT ON COLUMN public.file_update_releases.manifest_key_id IS
    'SHA-256 SPKI key ID from the detached manifest signature; the client still verifies the signature against its pinned trust set.';
COMMENT ON COLUMN public.file_update_releases.release_sequence IS
    'Strictly increasing sequence per role, architecture and channel, used for local anti-replay checks.';

CREATE FUNCTION public.compare_file_update_semver_v1(p_left text, p_right text)
RETURNS integer
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_left_without_build text;
    v_right_without_build text;
    v_left_core text;
    v_right_core text;
    v_left_prerelease text[];
    v_right_prerelease text[];
    v_left_core_parts text[];
    v_right_core_parts text[];
    v_left_separator integer;
    v_right_separator integer;
    v_left_numeric boolean;
    v_right_numeric boolean;
    v_index integer;
    v_left_number numeric;
    v_right_number numeric;
BEGIN
    v_left_without_build := split_part(p_left, '+', 1);
    v_right_without_build := split_part(p_right, '+', 1);
    v_left_separator := strpos(v_left_without_build, '-');
    v_right_separator := strpos(v_right_without_build, '-');
    IF v_left_separator = 0 THEN
        v_left_core := v_left_without_build;
        v_left_prerelease := NULL;
    ELSE
        v_left_core := substring(v_left_without_build FROM 1 FOR v_left_separator - 1);
        v_left_prerelease := string_to_array(substring(v_left_without_build FROM v_left_separator + 1), '.');
    END IF;
    IF v_right_separator = 0 THEN
        v_right_core := v_right_without_build;
        v_right_prerelease := NULL;
    ELSE
        v_right_core := substring(v_right_without_build FROM 1 FOR v_right_separator - 1);
        v_right_prerelease := string_to_array(substring(v_right_without_build FROM v_right_separator + 1), '.');
    END IF;

    v_left_core_parts := string_to_array(v_left_core, '.');
    v_right_core_parts := string_to_array(v_right_core, '.');
    FOR v_index IN 1..3 LOOP
        v_left_number := v_left_core_parts[v_index]::numeric;
        v_right_number := v_right_core_parts[v_index]::numeric;
        IF v_left_number < v_right_number THEN RETURN -1; END IF;
        IF v_left_number > v_right_number THEN RETURN 1; END IF;
    END LOOP;

    IF v_left_prerelease IS NULL OR v_right_prerelease IS NULL THEN
        IF v_left_prerelease IS NULL AND v_right_prerelease IS NULL THEN RETURN 0; END IF;
        RETURN CASE WHEN v_left_prerelease IS NULL THEN 1 ELSE -1 END;
    END IF;

    FOR v_index IN 1..greatest(cardinality(v_left_prerelease), cardinality(v_right_prerelease)) LOOP
        IF v_left_prerelease[v_index] IS NULL THEN RETURN -1; END IF;
        IF v_right_prerelease[v_index] IS NULL THEN RETURN 1; END IF;
        v_left_numeric := v_left_prerelease[v_index] ~ '^[0-9]+$';
        v_right_numeric := v_right_prerelease[v_index] ~ '^[0-9]+$';
        IF v_left_numeric AND v_right_numeric THEN
            v_left_number := v_left_prerelease[v_index]::numeric;
            v_right_number := v_right_prerelease[v_index]::numeric;
            IF v_left_number < v_right_number THEN RETURN -1; END IF;
            IF v_left_number > v_right_number THEN RETURN 1; END IF;
        ELSIF v_left_numeric THEN
            RETURN -1;
        ELSIF v_right_numeric THEN
            RETURN 1;
        ELSE
            IF v_left_prerelease[v_index] COLLATE "C" < v_right_prerelease[v_index] COLLATE "C" THEN RETURN -1; END IF;
            IF v_left_prerelease[v_index] COLLATE "C" > v_right_prerelease[v_index] COLLATE "C" THEN RETURN 1; END IF;
        END IF;
    END LOOP;
    RETURN 0;
END;
$function$;

REVOKE ALL ON FUNCTION public.compare_file_update_semver_v1(text, text)
    FROM PUBLIC, anon, authenticated, service_role;

CREATE INDEX file_update_releases_latest_idx
    ON public.file_update_releases (role, architecture, channel, release_sequence DESC)
    WHERE status = 'published';

ALTER TABLE public.file_update_releases ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.file_update_releases FROM PUBLIC, anon, authenticated, service_role;
GRANT SELECT ON TABLE public.file_update_releases TO service_role;
CREATE POLICY file_update_releases_service_read
    ON public.file_update_releases FOR SELECT TO service_role USING (true);

CREATE FUNCTION public.publish_file_update_release_v1(
    p_release_id uuid,
    p_role text,
    p_version text,
    p_channel text,
    p_release_sequence integer,
    p_manifest_size_bytes integer,
    p_manifest_sha256 text,
    p_manifest_key_id text
)
RETURNS uuid
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_product text;
    v_existing public.file_update_releases%ROWTYPE;
    v_latest_sequence integer;
    v_latest_version text;
BEGIN
    IF p_release_id IS NULL OR p_release_id = '00000000-0000-0000-0000-000000000000'::uuid OR
       p_release_id::text !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' THEN
        RAISE EXCEPTION 'file update release id is invalid';
    END IF;
    IF p_role = 'TeacherConsole' THEN
        v_product := 'VeyonCampus.TeacherConsole';
    ELSIF p_role = 'StudentSetup' THEN
        v_product := 'VeyonCampus.StudentSetup';
    ELSE
        RAISE EXCEPTION 'file update release role is invalid';
    END IF;
    IF p_version IS NULL OR length(p_version) > 64 OR
       p_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$' THEN
        RAISE EXCEPTION 'file update release version is invalid';
    END IF;
    IF p_channel IS NULL OR p_channel NOT IN ('stable', 'beta') THEN
        RAISE EXCEPTION 'file update release channel is invalid';
    END IF;
    IF p_channel = 'stable' AND position('-' IN p_version) > 0 THEN
        RAISE EXCEPTION 'stable file update release version cannot be a prerelease';
    END IF;
    IF p_release_sequence IS NULL OR p_release_sequence NOT BETWEEN 1 AND 2147483647 THEN
        RAISE EXCEPTION 'file update release sequence is invalid';
    END IF;
    IF p_manifest_size_bytes IS NULL OR p_manifest_size_bytes NOT BETWEEN 1 AND 16777216 THEN
        RAISE EXCEPTION 'file update manifest size is invalid';
    END IF;
    IF p_manifest_sha256 IS NULL OR p_manifest_sha256 !~ '^[a-f0-9]{64}$' OR
       p_manifest_key_id IS NULL OR p_manifest_key_id !~ '^[a-f0-9]{64}$' THEN
        RAISE EXCEPTION 'file update manifest digest or key id is invalid';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtext('veyon-file-update-release'), hashtext(p_role || ':' || p_channel));

    SELECT * INTO v_existing FROM public.file_update_releases
     WHERE release_id = p_release_id;
    IF FOUND THEN
        IF v_existing.role = p_role AND v_existing.product = v_product AND v_existing.version = p_version AND
           v_existing.channel = p_channel AND v_existing.release_sequence = p_release_sequence AND
           v_existing.manifest_size_bytes = p_manifest_size_bytes AND
           v_existing.manifest_sha256 = p_manifest_sha256 AND v_existing.manifest_key_id = p_manifest_key_id AND
           v_existing.status = 'published' THEN
            RETURN v_existing.release_id;
        END IF;
        RAISE EXCEPTION 'file update release id already exists with different metadata';
    END IF;

    SELECT * INTO v_existing FROM public.file_update_releases
     WHERE role = p_role AND architecture = 'win-x64' AND channel = p_channel AND version = p_version;
    IF FOUND THEN
        IF v_existing.release_sequence = p_release_sequence AND
           v_existing.manifest_size_bytes = p_manifest_size_bytes AND
           v_existing.manifest_sha256 = p_manifest_sha256 AND v_existing.manifest_key_id = p_manifest_key_id AND
           v_existing.status = 'published' THEN
            RETURN v_existing.release_id;
        END IF;
        RAISE EXCEPTION 'file update version already exists with different metadata';
    END IF;

    SELECT release_sequence, version INTO v_latest_sequence, v_latest_version
      FROM public.file_update_releases
     WHERE role = p_role AND architecture = 'win-x64' AND channel = p_channel
     ORDER BY release_sequence DESC
     LIMIT 1;
    v_latest_sequence := coalesce(v_latest_sequence, 0);
    IF p_release_sequence <= v_latest_sequence THEN
        RAISE EXCEPTION 'file update release sequence must increase';
    END IF;
    IF v_latest_version IS NOT NULL AND
       public.compare_file_update_semver_v1(p_version, v_latest_version) <= 0 THEN
        RAISE EXCEPTION 'file update release version must increase';
    END IF;

    INSERT INTO public.file_update_releases (
        release_id, role, product, version, architecture, channel, release_sequence,
        manifest_size_bytes, manifest_sha256, manifest_key_id, status, published_at
    ) VALUES (
        p_release_id, p_role, v_product, p_version, 'win-x64', p_channel, p_release_sequence,
        p_manifest_size_bytes, p_manifest_sha256, p_manifest_key_id, 'published', now()
    );
    RETURN p_release_id;
END;
$function$;

CREATE FUNCTION public.withdraw_file_update_release_v1(
    p_release_id uuid,
    p_reason text
)
RETURNS boolean
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF p_release_id IS NULL OR p_release_id = '00000000-0000-0000-0000-000000000000'::uuid OR
       p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 1 AND 500 THEN
        RAISE EXCEPTION 'file update withdrawal request is invalid';
    END IF;
    UPDATE public.file_update_releases
       SET status = 'withdrawn', withdrawn_at = now(), withdrawn_reason = btrim(p_reason)
     WHERE release_id = p_release_id AND status = 'published';
    RETURN FOUND;
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_file_update_release_v1(uuid, text, text, text, integer, integer, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_file_update_release_v1(uuid, text, text, text, integer, integer, text, text)
    TO service_role;
REVOKE ALL ON FUNCTION public.withdraw_file_update_release_v1(uuid, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.withdraw_file_update_release_v1(uuid, text)
    TO service_role;

COMMIT;
