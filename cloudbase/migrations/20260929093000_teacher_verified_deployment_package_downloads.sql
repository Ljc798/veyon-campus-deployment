-- Public uploads use a self-declared campus name and teacher identity. The
-- service stores keyed fingerprints only; the phone suffix is never exposed
-- through the student catalog or retained as clear text.

ALTER TABLE public.deployment_packages
    ALTER COLUMN campus_id DROP NOT NULL,
    ADD COLUMN publisher_identity_fingerprint char(64),
    ADD COLUMN publisher_phone_fingerprint char(64),
    ADD CONSTRAINT deployment_packages_publisher_fingerprints_rule
        CHECK (
            (publisher_identity_fingerprint IS NULL AND publisher_phone_fingerprint IS NULL)
            OR
            (publisher_identity_fingerprint IS NOT NULL
             AND publisher_phone_fingerprint IS NOT NULL
             AND publisher_identity_fingerprint ~ '^[A-F0-9]{64}$'
             AND publisher_phone_fingerprint ~ '^[A-F0-9]{64}$')
        );

-- Old clients must fail closed once the new verification contract is active.
DROP FUNCTION public.publish_deployment_package(uuid, bigint, text, integer, text, text);
DROP FUNCTION public.get_deployment_package_download(uuid);
DROP FUNCTION public.search_deployment_packages(text, bigint, integer, integer);

CREATE OR REPLACE FUNCTION public.validate_deployment_package_catalog_row()
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

        IF NEW.campus_id IS NOT NULL THEN
            SELECT campus.name
              INTO v_campus_name
              FROM public.campuses AS campus
             WHERE campus.id = NEW.campus_id
               AND campus.status = 'active';
            IF NOT FOUND THEN
                RAISE EXCEPTION 'package campus is missing or inactive';
            END IF;
            NEW.campus_name := v_campus_name;
        ELSIF NEW.campus_name IS NULL
           OR length(btrim(NEW.campus_name)) NOT BETWEEN 1 AND 100
           OR NEW.campus_name <> btrim(NEW.campus_name)
           OR NEW.campus_name ~ '[[:cntrl:]]' THEN
            RAISE EXCEPTION 'package campus name is invalid';
        ELSE
            -- Preserve the campus relation when the self-declared name matches
            -- exactly one active campus. Unregistered or ambiguous names remain
            -- publishable with campus_id NULL and receive global-only telemetry.
            SELECT min(campus.id)
              INTO NEW.campus_id
              FROM public.campuses AS campus
             WHERE campus.name = NEW.campus_name
               AND campus.status = 'active'
            HAVING count(*) = 1;
        END IF;

        IF NEW.publisher_identity_fingerprint IS NULL
           OR NEW.publisher_phone_fingerprint IS NULL THEN
            RAISE EXCEPTION 'teacher identity and download code are required';
        END IF;

        SELECT
            (NEW.created_by_user_id = 'public' AND NEW.campus_id IS NULL)
            OR EXISTS (
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
        NEW.publisher_identity_fingerprint, NEW.publisher_phone_fingerprint,
        NEW.created_by_user_id, NEW.created_at, NEW.published_at
    ) IS DISTINCT FROM ROW(
        OLD.package_id, OLD.campus_id, OLD.campus_name, OLD.computer_prefix,
        OLD.schema_version, OLD.target_os, OLD.architecture,
        OLD.artifact_size_bytes, OLD.artifact_sha256,
        OLD.publisher_identity_fingerprint, OLD.publisher_phone_fingerprint,
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

-- Packages published before teacher verification was introduced have no
-- verifiable download code. Withdraw them so every visible package requires
-- the teacher's phone suffix; publishers can re-publish them with the new UI.
UPDATE public.deployment_packages
   SET status = 'withdrawn',
       withdrawn_at = now(),
       withdrawn_by_user_id = 'migration',
       withdrawn_reason = 'Re-publish with teacher phone verification'
 WHERE status = 'published'
   AND (publisher_identity_fingerprint IS NULL OR publisher_phone_fingerprint IS NULL);

CREATE FUNCTION public.publish_deployment_package_public(
    p_package_id uuid,
    p_campus_name text,
    p_computer_prefix text,
    p_artifact_size_bytes integer,
    p_artifact_sha256 text,
    p_publisher_fingerprint text,
    p_phone_fingerprint text,
    p_created_by_user_id text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    INSERT INTO public.deployment_packages (
        package_id,
        campus_id,
        campus_name,
        computer_prefix,
        artifact_size_bytes,
        artifact_sha256,
        publisher_identity_fingerprint,
        publisher_phone_fingerprint,
        created_by_user_id
    ) VALUES (
        p_package_id,
        NULL,
        p_campus_name,
        p_computer_prefix,
        p_artifact_size_bytes,
        upper(p_artifact_sha256),
        upper(p_publisher_fingerprint),
        upper(p_phone_fingerprint),
        p_created_by_user_id
    );

    INSERT INTO public.deployment_package_artifacts (package_id)
    VALUES (p_package_id);
END;
$function$;

CREATE FUNCTION public.search_deployment_packages(
    p_query text,
    p_campus_id bigint,
    p_limit integer,
    p_offset integer
)
RETURNS TABLE(
    package_id uuid,
    campus_id bigint,
    display_name text,
    campus_name varchar,
    computer_prefix varchar,
    schema_version smallint,
    target_os text,
    architecture text,
    artifact_file_name text,
    artifact_size_bytes integer,
    artifact_sha256 text,
    download_count bigint,
    published_at timestamptz,
    requires_phone_verification boolean
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
    SELECT package.package_id,
           package.campus_id,
           package.display_name,
           package.campus_name,
           package.computer_prefix,
           package.schema_version,
           package.target_os,
           package.architecture,
           package.artifact_file_name,
           package.artifact_size_bytes,
           package.artifact_sha256,
           package.download_count,
           package.published_at,
           package.publisher_phone_fingerprint IS NOT NULL
      FROM public.deployment_packages AS package
     WHERE package.status = 'published'
       AND (p_campus_id IS NULL OR package.campus_id = p_campus_id)
       AND (
           NULLIF(btrim(p_query), '') IS NULL
           OR package.campus_name ILIKE '%' || replace(replace(replace(p_query, E'\\', E'\\\\'), '%', E'\\%'), '_', E'\\_') || '%' ESCAPE E'\\'
           OR package.computer_prefix ILIKE '%' || replace(replace(replace(p_query, E'\\', E'\\\\'), '%', E'\\%'), '_', E'\\_') || '%' ESCAPE E'\\'
       )
     ORDER BY package.published_at DESC, package.package_id DESC
     LIMIT least(greatest(p_limit, 1), 50)
    OFFSET least(greatest(p_offset, 0), 10000);
$function$;

CREATE FUNCTION public.search_deployment_packages_by_publisher(
    p_publisher_fingerprint text,
    p_limit integer,
    p_offset integer
)
RETURNS TABLE(
    package_id uuid,
    campus_id bigint,
    display_name text,
    campus_name varchar,
    computer_prefix varchar,
    schema_version smallint,
    target_os text,
    architecture text,
    artifact_file_name text,
    artifact_size_bytes integer,
    artifact_sha256 text,
    download_count bigint,
    published_at timestamptz,
    requires_phone_verification boolean
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
    SELECT package.package_id,
           package.campus_id,
           package.display_name,
           package.campus_name,
           package.computer_prefix,
           package.schema_version,
           package.target_os,
           package.architecture,
           package.artifact_file_name,
           package.artifact_size_bytes,
           package.artifact_sha256,
           package.download_count,
           package.published_at,
           package.publisher_phone_fingerprint IS NOT NULL
      FROM public.deployment_packages AS package
     WHERE package.status = 'published'
       AND package.publisher_identity_fingerprint = p_publisher_fingerprint
     ORDER BY package.published_at DESC, package.package_id DESC
     LIMIT least(greatest(p_limit, 1), 50)
    OFFSET least(greatest(p_offset, 0), 10000);
$function$;

CREATE FUNCTION public.get_deployment_package_download_with_phone(
    p_package_id uuid,
    p_phone_fingerprint text
)
RETURNS TABLE(
    package_id uuid,
    storage_key text,
    artifact_file_name text,
    artifact_size_bytes integer,
    artifact_sha256 text
)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
    SELECT package.package_id,
           artifact.storage_key,
           package.artifact_file_name,
           package.artifact_size_bytes,
           package.artifact_sha256
      FROM public.deployment_packages AS package
      JOIN public.deployment_package_artifacts AS artifact
        ON artifact.package_id = package.package_id
     WHERE package.package_id = p_package_id
       AND package.status = 'published'
       AND package.publisher_phone_fingerprint = p_phone_fingerprint;
$function$;

REVOKE ALL ON FUNCTION public.validate_deployment_package_catalog_row()
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.publish_deployment_package_public(uuid, text, text, integer, text, text, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.search_deployment_packages(text, bigint, integer, integer)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.search_deployment_packages_by_publisher(text, integer, integer)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.get_deployment_package_download_with_phone(uuid, text)
    FROM PUBLIC, anon, authenticated, service_role;

GRANT EXECUTE ON FUNCTION public.publish_deployment_package_public(uuid, text, text, integer, text, text, text, text)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.search_deployment_packages(text, bigint, integer, integer)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.search_deployment_packages_by_publisher(text, integer, integer)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.get_deployment_package_download_with_phone(uuid, text)
    TO service_role;
