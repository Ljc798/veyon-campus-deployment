-- Restore simple public package publishing: campus name, publisher name and
-- the teacher phone suffix are submitted with the validated package. The API
-- stores only a keyed fingerprint of the suffix; student downloads verify it.

BEGIN;

ALTER TABLE public.deployment_packages
    ADD COLUMN IF NOT EXISTS publisher_name varchar(100);

ALTER TABLE public.deployment_packages
    DROP CONSTRAINT IF EXISTS deployment_packages_artifact_size_bytes_check,
    ADD CONSTRAINT deployment_packages_artifact_size_bytes_check
        CHECK (artifact_size_bytes BETWEEN 1 AND 65536),
    ADD CONSTRAINT deployment_packages_publisher_name_rule
        CHECK (
            publisher_name IS NULL
            OR (length(btrim(publisher_name)) BETWEEN 1 AND 100
                AND publisher_name = btrim(publisher_name)
                AND publisher_name !~ '[[:cntrl:]]')
        );

COMMENT ON COLUMN public.deployment_packages.publisher_name IS
    'Teacher-provided publisher name retained for private operational attribution; never included in student catalog responses.';
COMMENT ON COLUMN public.deployment_packages.publisher_identity_fingerprint IS
    'Keyed HMAC fingerprint of the teacher-provided name; not an authentication credential.';
COMMENT ON COLUMN public.deployment_packages.publisher_phone_fingerprint IS
    'Keyed HMAC fingerprint of the teacher phone last four digits, used as the student download verifier; raw digits are not stored.';

UPDATE storage.buckets
   SET file_size_limit = 65536
 WHERE id = 'deployment-package-artifacts';

CREATE OR REPLACE FUNCTION public.validate_deployment_package_catalog_row()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_campus_name varchar(100);
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status <> 'published' THEN
            RAISE EXCEPTION 'packages must pass server validation before publication';
        END IF;

        IF NEW.artifact_size_bytes NOT BETWEEN 1 AND 65536 THEN
            RAISE EXCEPTION 'package exceeds the 64 KiB limit';
        END IF;

        IF NEW.publisher_name IS NULL
           OR length(btrim(NEW.publisher_name)) NOT BETWEEN 1 AND 100
           OR NEW.publisher_name <> btrim(NEW.publisher_name)
           OR NEW.publisher_name ~ '[[:cntrl:]]' THEN
            RAISE EXCEPTION 'publisher name is invalid';
        END IF;

        IF NEW.publisher_identity_fingerprint IS NULL
           OR NEW.publisher_identity_fingerprint !~ '^[A-F0-9]{64}$'
           OR NEW.publisher_phone_fingerprint IS NULL
           OR NEW.publisher_phone_fingerprint !~ '^[A-F0-9]{64}$' THEN
            RAISE EXCEPTION 'teacher name and phone suffix are required';
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
            -- Attach an existing campus only when the name resolves uniquely;
            -- unregistered or ambiguous names remain searchable by their text.
            SELECT min(campus.id)
              INTO NEW.campus_id
              FROM public.campuses AS campus
             WHERE campus.name = NEW.campus_name
               AND campus.status = 'active'
            HAVING count(*) = 1;
        END IF;

        NEW.created_by_user_id := 'public';
        NEW.published_at := COALESCE(NEW.published_at, now());
        RETURN NEW;
    END IF;

    IF ROW(
        NEW.package_id, NEW.campus_id, NEW.campus_name, NEW.computer_prefix,
        NEW.schema_version, NEW.target_os, NEW.architecture,
        NEW.artifact_size_bytes, NEW.artifact_sha256,
        NEW.publisher_name, NEW.publisher_identity_fingerprint,
        NEW.publisher_phone_fingerprint, NEW.created_by_user_id,
        NEW.created_at, NEW.published_at
    ) IS DISTINCT FROM ROW(
        OLD.package_id, OLD.campus_id, OLD.campus_name, OLD.computer_prefix,
        OLD.schema_version, OLD.target_os, OLD.architecture,
        OLD.artifact_size_bytes, OLD.artifact_sha256,
        OLD.publisher_name, OLD.publisher_identity_fingerprint,
        OLD.publisher_phone_fingerprint, OLD.created_by_user_id,
        OLD.created_at, OLD.published_at
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

DROP FUNCTION IF EXISTS public.publish_deployment_package_authenticated(
    uuid, bigint, text, integer, text, text, text, text
);
DROP FUNCTION IF EXISTS public.publish_deployment_package_public(
    uuid, text, text, integer, text, text, text, text
);

CREATE FUNCTION public.publish_deployment_package_public(
    p_package_id uuid,
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
        package_id,
        campus_id,
        campus_name,
        computer_prefix,
        artifact_size_bytes,
        artifact_sha256,
        publisher_name,
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
        p_publisher_name,
        upper(p_publisher_identity_fingerprint),
        upper(p_phone_fingerprint),
        'public'
    );

    INSERT INTO public.deployment_package_artifacts (package_id)
    VALUES (p_package_id);
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_deployment_package_public(
    uuid, text, text, text, integer, text, text, text
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_deployment_package_public(
    uuid, text, text, text, integer, text, text, text
) TO service_role;

COMMIT;
