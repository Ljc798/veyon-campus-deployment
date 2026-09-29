-- Public teacher-app publishing is mediated by the server service role. The
-- publisher marker is written only by that trusted API after validating the
-- package, active campus, campus name, and strict size limits.

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
            NEW.created_by_user_id = 'public'
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

UPDATE storage.buckets
   SET file_size_limit = 65536
 WHERE id = 'deployment-package-artifacts';
