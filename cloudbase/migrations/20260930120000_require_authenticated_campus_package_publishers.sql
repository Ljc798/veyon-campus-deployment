-- Replace anonymous deployment package publication with authenticated campus
-- publisher authorization. Package bytes remain in the private storage bucket.

BEGIN;

DROP FUNCTION IF EXISTS public.publish_deployment_package_public(
    uuid, text, text, integer, text, text, text, text
);

CREATE OR REPLACE FUNCTION public.set_deployment_package_publisher(
    p_user_id text,
    p_campus_id bigint,
    p_is_active boolean,
    p_actor_user_id text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    IF p_user_id IS NULL OR length(btrim(p_user_id)) NOT BETWEEN 1 AND 64 OR
       p_campus_id IS NULL OR p_campus_id <= 0 OR p_is_active IS NULL THEN
        RAISE EXCEPTION 'publisher grant parameters are invalid';
    END IF;

    PERFORM 1
      FROM public.admin_profiles AS profile
     WHERE profile.user_id = p_actor_user_id
       AND profile.role IN ('owner', 'admin')
     FOR SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'only owner/admin may manage package publishers';
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM public.campuses AS campus
         WHERE campus.id = p_campus_id
    ) THEN
        RAISE EXCEPTION 'campus does not exist';
    END IF;

    INSERT INTO public.deployment_package_publishers (
        user_id, campus_id, is_active, created_by_user_id
    ) VALUES (
        p_user_id, p_campus_id, p_is_active, p_actor_user_id
    )
    ON CONFLICT (user_id, campus_id)
    DO UPDATE SET is_active = EXCLUDED.is_active;
END;
$function$;

-- CREATE OR REPLACE preserves the ACL from the previous migration, but keep
-- the service-only boundary explicit in this migration too: the actor ID is
-- supplied by the API server after CloudBase Auth verification.
REVOKE ALL ON FUNCTION public.set_deployment_package_publisher(text, bigint, boolean, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.set_deployment_package_publisher(text, bigint, boolean, text)
    TO service_role;

CREATE FUNCTION public.publish_deployment_package_authenticated(
    p_package_id uuid,
    p_campus_id bigint,
    p_computer_prefix text,
    p_artifact_size_bytes integer,
    p_artifact_sha256 text,
    p_publisher_identity_fingerprint text,
    p_download_code_fingerprint text,
    p_created_by_user_id text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_campus_name public.campuses.name%TYPE;
BEGIN
    IF p_created_by_user_id IS NULL
       OR length(btrim(p_created_by_user_id)) NOT BETWEEN 1 AND 64 THEN
        RAISE EXCEPTION 'authenticated publisher user ID is invalid';
    END IF;

    -- Re-check the grant inside the same transaction as publication. Locking
    -- the active grant makes a concurrent revoke wait until this publication
    -- commits; an already committed revoke is observed as inactive here.
    PERFORM 1
      FROM public.deployment_package_publishers AS publisher
     WHERE publisher.user_id = p_created_by_user_id
       AND publisher.campus_id = p_campus_id
       AND publisher.is_active
     FOR SHARE;

    IF NOT FOUND THEN
        -- Owner/admin users may publish without a separate campus grant, in
        -- line with resolve_deployment_package_publisher(). Lock the role row
        -- so a concurrent role downgrade is serialized with this publication.
        PERFORM 1
          FROM public.admin_profiles AS profile
         WHERE profile.user_id = p_created_by_user_id
           AND profile.role IN ('owner', 'admin')
         FOR SHARE;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'publisher is not authorized';
        END IF;
    END IF;

    -- Serialize publication with campus pause/rename operations as well.
    SELECT campus.name
      INTO v_campus_name
      FROM public.campuses AS campus
     WHERE campus.id = p_campus_id
       AND campus.status = 'active'
     FOR SHARE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'package campus is missing or inactive';
    END IF;

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
    )
    VALUES (
        p_package_id,
        p_campus_id,
        v_campus_name,
        p_computer_prefix,
        p_artifact_size_bytes,
        upper(p_artifact_sha256),
        upper(p_publisher_identity_fingerprint),
        upper(p_download_code_fingerprint),
        p_created_by_user_id
    );

    INSERT INTO public.deployment_package_artifacts (package_id)
    VALUES (p_package_id);
END;
$function$;

COMMENT ON COLUMN public.deployment_packages.publisher_phone_fingerprint IS
    'Legacy column name retained for compatibility; stores only a keyed fingerprint of the four-digit package download verification value.';

REVOKE ALL ON FUNCTION public.publish_deployment_package_authenticated(
    uuid, bigint, text, integer, text, text, text, text
) FROM PUBLIC, anon, authenticated, service_role;

GRANT EXECUTE ON FUNCTION public.publish_deployment_package_authenticated(
    uuid, bigint, text, integer, text, text, text, text
) TO service_role;

COMMIT;
