BEGIN;

-- Keep the HMAC key version with each immutable package so the service can
-- verify phone suffixes and publisher identity using the key that created it.
ALTER TABLE public.deployment_packages
    ADD COLUMN publisher_fingerprint_key_id varchar(64) NOT NULL DEFAULT 'legacy',
    ADD CONSTRAINT deployment_packages_fingerprint_key_id_rule
        CHECK (publisher_fingerprint_key_id ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$');

COMMENT ON COLUMN public.deployment_packages.publisher_fingerprint_key_id IS
    'Non-secret identifier of the server HMAC key version used for publisher and phone fingerprints.';

CREATE FUNCTION public.set_deployment_package_fingerprint_key_id()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_key_id text := current_setting('veyon.deployment_package_fingerprint_key_id', true);
BEGIN
    IF v_key_id IS NOT NULL AND v_key_id <> '' THEN
        IF v_key_id !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' THEN
            RAISE EXCEPTION 'package fingerprint key identifier is invalid';
        END IF;
        NEW.publisher_fingerprint_key_id := v_key_id;
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER deployment_packages_set_fingerprint_key_id
    BEFORE INSERT ON public.deployment_packages
    FOR EACH ROW
    EXECUTE FUNCTION public.set_deployment_package_fingerprint_key_id();

CREATE FUNCTION public.prevent_deployment_package_fingerprint_key_id_change()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF NEW.publisher_fingerprint_key_id IS DISTINCT FROM OLD.publisher_fingerprint_key_id THEN
        RAISE EXCEPTION 'published package fingerprint key version is immutable';
    END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER deployment_packages_fingerprint_key_id_immutable
    BEFORE UPDATE OF publisher_fingerprint_key_id ON public.deployment_packages
    FOR EACH ROW
    EXECUTE FUNCTION public.prevent_deployment_package_fingerprint_key_id_change();

REVOKE ALL ON FUNCTION public.set_deployment_package_fingerprint_key_id()
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.prevent_deployment_package_fingerprint_key_id_change()
    FROM PUBLIC, anon, authenticated, service_role;

-- Preserve the existing nine-argument RPC for rolling-deploy compatibility.
-- New callers pass the key ID; the insert trigger records it on the package.
CREATE FUNCTION public.publish_deployment_package_public(
    p_package_id uuid,
    p_schema_version integer,
    p_campus_name text,
    p_publisher_name text,
    p_computer_prefix text,
    p_artifact_size_bytes integer,
    p_artifact_sha256 text,
    p_publisher_identity_fingerprint text,
    p_phone_fingerprint text,
    p_fingerprint_key_id text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF p_fingerprint_key_id IS NULL
       OR p_fingerprint_key_id !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' THEN
        RAISE EXCEPTION 'package fingerprint key identifier is invalid';
    END IF;

    PERFORM set_config('veyon.deployment_package_fingerprint_key_id', p_fingerprint_key_id, true);
    PERFORM public.publish_deployment_package_public(
        p_package_id,
        p_schema_version,
        p_campus_name,
        p_publisher_name,
        p_computer_prefix,
        p_artifact_size_bytes,
        p_artifact_sha256,
        p_publisher_identity_fingerprint,
        p_phone_fingerprint
    );
    PERFORM set_config('veyon.deployment_package_fingerprint_key_id', '', true);
END;
$function$;

REVOKE ALL ON FUNCTION public.publish_deployment_package_public(
    uuid, integer, text, text, text, integer, text, text, text, text
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.publish_deployment_package_public(
    uuid, integer, text, text, text, integer, text, text, text, text
) TO service_role;

-- This overload selects the candidate phone fingerprint using the package's
-- stored key ID, then delegates rate limiting and authorization to the
-- existing three-argument RPC.
CREATE FUNCTION public.get_deployment_package_download_with_rate_limit(
    p_package_id uuid,
    p_phone_fingerprint text,
    p_client_fingerprint text,
    p_phone_fingerprints jsonb
)
RETURNS TABLE(
    decision text,
    retry_after_seconds integer,
    package_id uuid,
    storage_key text,
    artifact_file_name text,
    artifact_size_bytes integer,
    artifact_sha256 text
)
LANGUAGE plpgsql
VOLATILE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_key_id text;
    v_phone_fingerprint text;
BEGIN
    IF jsonb_typeof(p_phone_fingerprints) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'download fingerprint key set is invalid';
    END IF;
    IF (SELECT count(*) FROM jsonb_object_keys(p_phone_fingerprints)) NOT BETWEEN 1 AND 8 THEN
        RAISE EXCEPTION 'download fingerprint key set is invalid';
    END IF;
    IF EXISTS (
        SELECT 1
          FROM jsonb_each_text(p_phone_fingerprints) AS supplied(key_id, fingerprint)
         WHERE supplied.key_id !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'
            OR upper(supplied.fingerprint) !~ '^[A-F0-9]{64}$'
    ) THEN
        RAISE EXCEPTION 'download fingerprint key set is invalid';
    END IF;

    SELECT package.publisher_fingerprint_key_id
      INTO v_key_id
      FROM public.deployment_packages AS package
     WHERE package.package_id = p_package_id
       AND package.status = 'published';

    IF NOT FOUND THEN
        RETURN QUERY
        SELECT * FROM public.get_deployment_package_download_with_rate_limit(
            p_package_id, p_phone_fingerprint, p_client_fingerprint
        );
        RETURN;
    END IF;

    v_phone_fingerprint := p_phone_fingerprints ->> v_key_id;
    IF v_phone_fingerprint IS NULL THEN
        RETURN QUERY SELECT 'key_unavailable'::text, 0::integer, NULL::uuid, NULL::text,
                            NULL::text, NULL::integer, NULL::text;
        RETURN;
    END IF;

    IF upper(v_phone_fingerprint) !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'download fingerprint is invalid';
    END IF;

    RETURN QUERY
    SELECT * FROM public.get_deployment_package_download_with_rate_limit(
        p_package_id, upper(v_phone_fingerprint), p_client_fingerprint
    );
END;
$function$;

REVOKE ALL ON FUNCTION public.get_deployment_package_download_with_rate_limit(uuid, text, text, jsonb)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.get_deployment_package_download_with_rate_limit(uuid, text, text, jsonb)
    TO service_role;

-- Keep authenticated publisher lookup working across key rotations by
-- matching against the supplied, server-generated key-ID/fingerprint pairs.
CREATE FUNCTION public.search_deployment_packages_by_publisher(
    p_publisher_fingerprint text,
    p_limit integer,
    p_offset integer,
    p_publisher_fingerprints jsonb
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
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF p_publisher_fingerprint IS NULL
       OR upper(p_publisher_fingerprint) !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'publisher fingerprint key set is invalid';
    END IF;
    IF jsonb_typeof(p_publisher_fingerprints) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION 'publisher fingerprint key set is invalid';
    END IF;
    IF (SELECT count(*) FROM jsonb_object_keys(p_publisher_fingerprints)) NOT BETWEEN 1 AND 8 THEN
        RAISE EXCEPTION 'publisher fingerprint key set is invalid';
    END IF;
    IF EXISTS (
        SELECT 1
          FROM jsonb_each_text(p_publisher_fingerprints) AS supplied(key_id, fingerprint)
         WHERE supplied.key_id !~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'
            OR upper(supplied.fingerprint) !~ '^[A-F0-9]{64}$'
    ) THEN
        RAISE EXCEPTION 'publisher fingerprint key set is invalid';
    END IF;

    RETURN QUERY
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
       AND EXISTS (
            SELECT 1
              FROM jsonb_each_text(p_publisher_fingerprints) AS supplied(key_id, fingerprint)
             WHERE supplied.key_id = package.publisher_fingerprint_key_id
               AND upper(supplied.fingerprint) = package.publisher_identity_fingerprint
       )
     ORDER BY package.published_at DESC, package.package_id DESC
     LIMIT least(greatest(p_limit, 1), 50)
    OFFSET least(greatest(p_offset, 0), 10000);
END;
$function$;

REVOKE ALL ON FUNCTION public.search_deployment_packages_by_publisher(text, integer, integer, jsonb)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.search_deployment_packages_by_publisher(text, integer, integer, jsonb)
    TO service_role;

NOTIFY pgrst, 'reload schema';

COMMIT;
