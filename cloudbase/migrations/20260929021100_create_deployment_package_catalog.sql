-- Cloud-hosted catalog for teacher-published Veyon Campus configuration packages.
-- Package bytes live in a private CloudBase storage bucket; PostgreSQL stores only
-- searchable metadata and a server-only deterministic object key.

CREATE TABLE public.deployment_package_publishers (
    user_id varchar(64) NOT NULL
        CHECK (length(btrim(user_id)) BETWEEN 1 AND 64),
    campus_id bigint NOT NULL
        REFERENCES public.campuses(id) ON DELETE RESTRICT,
    is_active boolean NOT NULL DEFAULT true,
    created_by_user_id varchar(64) NOT NULL
        CHECK (length(btrim(created_by_user_id)) BETWEEN 1 AND 64),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, campus_id)
);

COMMENT ON TABLE public.deployment_package_publishers IS
    'Server-managed authorization mapping for teachers allowed to publish packages for a campus.';

CREATE TABLE public.deployment_packages (
    package_id uuid PRIMARY KEY,
    campus_id bigint NOT NULL
        REFERENCES public.campuses(id) ON DELETE RESTRICT,
    campus_name varchar(100) NOT NULL,
    computer_prefix varchar(12) NOT NULL
        CHECK (
            computer_prefix ~ '^[A-Za-z0-9][A-Za-z0-9-]{0,11}$'
            AND computer_prefix !~ '-$'
            AND computer_prefix ~ '[A-Za-z]'
        ),
    display_name text GENERATED ALWAYS AS (campus_name || ' / ' || computer_prefix) STORED,
    schema_version smallint NOT NULL DEFAULT 3 CHECK (schema_version = 3),
    target_os text NOT NULL DEFAULT 'windows' CHECK (target_os = 'windows'),
    architecture text NOT NULL DEFAULT 'x64' CHECK (architecture = 'x64'),
    artifact_file_name text GENERATED ALWAYS AS (
        'veyon-campus-config-v3-' || replace(package_id::text, '-', '') || '.zip'
    ) STORED,
    artifact_size_bytes integer NOT NULL
        CHECK (artifact_size_bytes BETWEEN 1 AND 524288),
    artifact_sha256 text NOT NULL
        CHECK (length(artifact_sha256) = 64 AND artifact_sha256 ~ '^[A-F0-9]{64}$'),
    status text NOT NULL DEFAULT 'published'
        CHECK (status IN ('published', 'withdrawn')),
    created_by_user_id varchar(64) NOT NULL
        CHECK (length(btrim(created_by_user_id)) BETWEEN 1 AND 64),
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NOT NULL DEFAULT now(),
    download_count bigint NOT NULL DEFAULT 0 CHECK (download_count >= 0),
    withdrawn_at timestamptz,
    withdrawn_by_user_id varchar(64),
    withdrawn_reason text,
    CONSTRAINT deployment_packages_file_name_rule
        CHECK (artifact_file_name ~ '^veyon-campus-config-v3-[a-f0-9]{32}\.zip$'),
    CONSTRAINT deployment_packages_withdrawal_state
        CHECK (
            (status = 'published' AND withdrawn_at IS NULL
                AND withdrawn_by_user_id IS NULL AND withdrawn_reason IS NULL)
            OR
            (status = 'withdrawn' AND withdrawn_at IS NOT NULL
                AND withdrawn_by_user_id IS NOT NULL
                AND length(btrim(withdrawn_by_user_id)) BETWEEN 1 AND 64)
        )
);

COMMENT ON TABLE public.deployment_packages IS
    'One immutable published v3 campus configuration package per manifest packageId; only published catalog fields are readable by students.';
COMMENT ON COLUMN public.deployment_packages.artifact_file_name IS
    'Canonical download name generated from the manifest UUID; never accepted from the uploader.';

CREATE TABLE public.deployment_package_artifacts (
    package_id uuid PRIMARY KEY
        REFERENCES public.deployment_packages(package_id) ON DELETE RESTRICT,
    storage_key text GENERATED ALWAYS AS (
        'deployment-packages/v3/' || replace(package_id::text, '-', '') || '.zip'
    ) STORED,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT deployment_package_artifacts_storage_key_rule
        CHECK (storage_key ~ '^deployment-packages/v3/[a-f0-9]{32}\.zip$'),
    UNIQUE (storage_key)
);

COMMENT ON TABLE public.deployment_package_artifacts IS
    'Private object-storage locator for each package. This table is server-only and is never queried by student clients.';

CREATE INDEX deployment_packages_public_campus_idx
    ON public.deployment_packages (campus_id, published_at DESC, package_id DESC)
    WHERE status = 'published';
CREATE INDEX deployment_packages_public_campus_name_idx
    ON public.deployment_packages (lower(campus_name) text_pattern_ops, published_at DESC)
    WHERE status = 'published';
CREATE INDEX deployment_packages_public_prefix_idx
    ON public.deployment_packages (lower(computer_prefix) text_pattern_ops, published_at DESC)
    WHERE status = 'published';

CREATE FUNCTION public.validate_deployment_package_catalog_row()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_campus_name varchar(100);
    v_can_publish boolean;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status <> 'published' THEN
            RAISE EXCEPTION 'packages must pass server validation before publication';
        END IF;

        SELECT campus.name
          INTO v_campus_name
          FROM public.campuses AS campus
         WHERE campus.id = NEW.campus_id
           AND campus.status = 'active';
        IF NOT FOUND THEN
            RAISE EXCEPTION 'package campus is missing or inactive';
        END IF;
        NEW.campus_name := v_campus_name;

        SELECT
            EXISTS (
                SELECT 1
                  FROM public.deployment_package_publishers AS publisher
                 WHERE publisher.user_id = NEW.created_by_user_id
                   AND publisher.campus_id = NEW.campus_id
                   AND publisher.is_active
            )
            OR EXISTS (
                SELECT 1
                  FROM public.admin_profiles AS profile
                 WHERE profile.user_id = NEW.created_by_user_id
                   AND profile.role IN ('owner', 'admin')
            )
          INTO v_can_publish;
        IF NOT v_can_publish THEN
            RAISE EXCEPTION 'publisher is not authorized for this campus';
        END IF;

        NEW.published_at := COALESCE(NEW.published_at, now());
        RETURN NEW;
    END IF;

    IF ROW(
        NEW.package_id, NEW.campus_id, NEW.campus_name, NEW.computer_prefix,
        NEW.schema_version, NEW.target_os, NEW.architecture,
        NEW.artifact_size_bytes, NEW.artifact_sha256,
        NEW.created_by_user_id, NEW.created_at, NEW.published_at
    ) IS DISTINCT FROM ROW(
        OLD.package_id, OLD.campus_id, OLD.campus_name, OLD.computer_prefix,
        OLD.schema_version, OLD.target_os, OLD.architecture,
        OLD.artifact_size_bytes, OLD.artifact_sha256,
        OLD.created_by_user_id, OLD.created_at, OLD.published_at
    ) THEN
        RAISE EXCEPTION 'published package metadata is immutable; publish a new package instead';
    END IF;

    IF NEW.download_count < OLD.download_count THEN
        RAISE EXCEPTION 'package download count cannot decrease';
    END IF;

    IF OLD.status = 'withdrawn' THEN
        IF NEW.status <> 'withdrawn'
           OR ROW(NEW.withdrawn_at, NEW.withdrawn_by_user_id, NEW.withdrawn_reason)
              IS DISTINCT FROM ROW(OLD.withdrawn_at, OLD.withdrawn_by_user_id, OLD.withdrawn_reason) THEN
            RAISE EXCEPTION 'withdrawn packages cannot be republished or edited';
        END IF;
        RETURN NEW;
    END IF;

    IF NEW.status = 'withdrawn' THEN
        IF NEW.withdrawn_by_user_id IS NULL OR length(btrim(NEW.withdrawn_by_user_id)) NOT BETWEEN 1 AND 64 THEN
            RAISE EXCEPTION 'withdrawal requires an authenticated operator';
        END IF;
        NEW.withdrawn_at := COALESCE(NEW.withdrawn_at, now());
    ELSIF NEW.status <> OLD.status
       OR NEW.withdrawn_at IS NOT NULL
       OR NEW.withdrawn_by_user_id IS NOT NULL
       OR NEW.withdrawn_reason IS NOT NULL THEN
        RAISE EXCEPTION 'unsupported package status transition';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE TRIGGER deployment_packages_validate_row
BEFORE INSERT OR UPDATE ON public.deployment_packages
FOR EACH ROW EXECUTE FUNCTION public.validate_deployment_package_catalog_row();

CREATE FUNCTION public.require_deployment_package_artifact()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF NEW.status = 'published'
       AND NOT EXISTS (
           SELECT 1
             FROM public.deployment_package_artifacts AS artifact
            WHERE artifact.package_id = NEW.package_id
       ) THEN
        RAISE EXCEPTION 'published package must have a private storage artifact';
    END IF;
    RETURN NULL;
END;
$function$;

CREATE CONSTRAINT TRIGGER deployment_packages_require_artifact
AFTER INSERT OR UPDATE ON public.deployment_packages
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION public.require_deployment_package_artifact();

CREATE FUNCTION public.record_deployment_package_download(p_package_id uuid)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_download_count bigint;
BEGIN
    UPDATE public.deployment_packages AS package
       SET download_count = package.download_count + 1
     WHERE package.package_id = p_package_id
       AND package.status = 'published'
    RETURNING package.download_count INTO v_download_count;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'published package not found';
    END IF;
    RETURN v_download_count;
END;
$function$;

REVOKE ALL ON FUNCTION public.validate_deployment_package_catalog_row()
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.require_deployment_package_artifact()
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.record_deployment_package_download(uuid)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.record_deployment_package_download(uuid)
    TO service_role;

REVOKE ALL ON TABLE public.deployment_package_publishers
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON TABLE public.deployment_packages
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON TABLE public.deployment_package_artifacts
    FROM PUBLIC, anon, authenticated, service_role;

GRANT USAGE ON SCHEMA public TO anon, authenticated, service_role;
GRANT SELECT (user_id, campus_id, is_active, created_at)
    ON TABLE public.deployment_package_publishers TO authenticated;
GRANT SELECT (package_id, display_name, campus_name, computer_prefix,
              schema_version, target_os, architecture, artifact_file_name,
              artifact_size_bytes, artifact_sha256, download_count, published_at)
    ON TABLE public.deployment_packages TO anon, authenticated;

GRANT ALL ON TABLE public.deployment_package_publishers TO service_role;
GRANT ALL ON TABLE public.deployment_packages TO service_role;
GRANT ALL ON TABLE public.deployment_package_artifacts TO service_role;

ALTER TABLE public.deployment_package_publishers ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.deployment_packages ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.deployment_package_artifacts ENABLE ROW LEVEL SECURITY;

CREATE POLICY deployment_publishers_read_self ON public.deployment_package_publishers
    FOR SELECT TO authenticated
    USING (user_id = (SELECT auth.uid()) AND is_active);

CREATE POLICY deployment_packages_student_catalog_read ON public.deployment_packages
    FOR SELECT TO anon, authenticated
    USING (status = 'published' AND published_at <= now());

CREATE POLICY deployment_publishers_service_access ON public.deployment_package_publishers
    FOR ALL TO service_role USING (true) WITH CHECK (true);
CREATE POLICY deployment_packages_service_access ON public.deployment_packages
    FOR ALL TO service_role USING (true) WITH CHECK (true);
CREATE POLICY deployment_package_artifacts_service_access ON public.deployment_package_artifacts
    FOR ALL TO service_role USING (true) WITH CHECK (true);
