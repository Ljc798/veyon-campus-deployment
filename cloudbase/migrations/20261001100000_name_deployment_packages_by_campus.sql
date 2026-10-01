BEGIN;

-- Keep names readable in CloudBase Storage while retaining packageId-based
-- uniqueness for multiple immutable versions of the same campus package.
CREATE OR REPLACE FUNCTION public.deployment_package_campus_file_stem(p_campus_name text)
RETURNS text
LANGUAGE sql
IMMUTABLE
STRICT
SET search_path = pg_catalog, public
AS $function$
    SELECT COALESCE(
        NULLIF(
            btrim(regexp_replace(p_campus_name, '[[:cntrl:]<>:"/\\|?*]', '-', 'g'), ' .'),
            ''
        ),
        'campus'
    );
$function$;

ALTER TABLE public.deployment_packages
    DROP CONSTRAINT deployment_packages_file_name_rule,
    DROP COLUMN artifact_file_name;

ALTER TABLE public.deployment_packages
    ADD COLUMN artifact_file_name text GENERATED ALWAYS AS (
        public.deployment_package_campus_file_stem(campus_name)
        || '-' || replace(package_id::text, '-', '') || '.zip'
    ) STORED,
    ADD CONSTRAINT deployment_packages_file_name_rule
        CHECK (
            right(artifact_file_name, 37) = '-' || replace(package_id::text, '-', '') || '.zip'
            AND position('/' in artifact_file_name) = 0
            AND position(chr(92) in artifact_file_name) = 0
        );

COMMENT ON COLUMN public.deployment_packages.artifact_file_name IS
    'Canonical campus-named download file with packageId suffix; never accepted from the uploader.';

ALTER TABLE public.deployment_package_artifacts
    DROP CONSTRAINT deployment_package_artifacts_storage_key_rule;

ALTER TABLE public.deployment_package_artifacts
    ALTER COLUMN storage_key DROP EXPRESSION,
    ALTER COLUMN storage_key SET NOT NULL,
    ADD CONSTRAINT deployment_package_artifacts_storage_key_rule
        CHECK (
            storage_key LIKE 'deployment-packages/v3/%.zip'
            AND position('/' in substring(storage_key FROM length('deployment-packages/v3/') + 1)) = 0
            AND position(chr(92) in substring(storage_key FROM length('deployment-packages/v3/') + 1)) = 0
            AND substring(storage_key FROM length('deployment-packages/v3/') + 1) !~ '[[:cntrl:]]'
        );

CREATE FUNCTION public.set_deployment_package_campus_storage_key()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_file_name text;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF ROW(NEW.package_id, NEW.storage_key) IS DISTINCT FROM ROW(OLD.package_id, OLD.storage_key) THEN
            RAISE EXCEPTION 'published package storage keys are immutable';
        END IF;
        RETURN NEW;
    END IF;

    SELECT package.artifact_file_name
      INTO v_file_name
      FROM public.deployment_packages AS package
     WHERE package.package_id = NEW.package_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'deployment package must exist before its storage artifact';
    END IF;

    NEW.storage_key := 'deployment-packages/v3/' || v_file_name;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER deployment_package_artifacts_set_campus_storage_key
BEFORE INSERT OR UPDATE ON public.deployment_package_artifacts
FOR EACH ROW EXECUTE FUNCTION public.set_deployment_package_campus_storage_key();

COMMENT ON TABLE public.deployment_package_artifacts IS
    'Private object-storage locator for each package, named from the campus and a unique packageId suffix. This table is server-only and is never queried by student clients.';

GRANT SELECT (artifact_file_name) ON public.deployment_packages TO anon, authenticated, service_role;

COMMIT;
