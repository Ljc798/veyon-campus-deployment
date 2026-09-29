-- Private PG Storage bucket and transactional RPCs for deployment package APIs.
-- Package bytes are stored as canonical ZIP objects. Bucket/object RLS stays
-- private to service_role; anon/authenticated receive no object policies.

INSERT INTO storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
VALUES (
    'deployment-package-artifacts',
    'Deployment package artifacts',
    false,
    524288,
    ARRAY['application/zip']::text[]
);

CREATE FUNCTION public.resolve_deployment_package_publisher(
    p_campus_id bigint,
    p_user_id text
)
RETURNS TABLE(campus_name varchar, authorized boolean)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
    SELECT campus.name,
           EXISTS (
               SELECT 1
                 FROM public.deployment_package_publishers AS publisher
                WHERE publisher.user_id = p_user_id
                  AND publisher.campus_id = campus.id
                  AND publisher.is_active
           )
           OR EXISTS (
               SELECT 1
                 FROM public.admin_profiles AS profile
                WHERE profile.user_id = p_user_id
                  AND profile.role IN ('owner', 'admin')
           ) AS authorized
      FROM public.campuses AS campus
     WHERE campus.id = p_campus_id
       AND campus.status = 'active';
$function$;

CREATE FUNCTION public.publish_deployment_package(
    p_package_id uuid,
    p_campus_id bigint,
    p_computer_prefix text,
    p_artifact_size_bytes integer,
    p_artifact_sha256 text,
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
        computer_prefix,
        artifact_size_bytes,
        artifact_sha256,
        created_by_user_id
    ) VALUES (
        p_package_id,
        p_campus_id,
        p_computer_prefix,
        p_artifact_size_bytes,
        upper(p_artifact_sha256),
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
    published_at timestamptz
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
           package.published_at
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

CREATE FUNCTION public.get_deployment_package_download(p_package_id uuid)
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
       AND package.status = 'published';
$function$;

CREATE FUNCTION public.set_deployment_package_publisher(
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
    IF NOT EXISTS (
        SELECT 1
          FROM public.admin_profiles AS profile
         WHERE profile.user_id = p_actor_user_id
           AND profile.role IN ('owner', 'admin')
    ) THEN
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

CREATE FUNCTION public.withdraw_deployment_package(
    p_package_id uuid,
    p_actor_user_id text,
    p_reason text
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
BEGIN
    UPDATE public.deployment_packages AS package
       SET status = 'withdrawn',
           withdrawn_by_user_id = p_actor_user_id,
           withdrawn_reason = NULLIF(left(btrim(p_reason), 500), '')
     WHERE package.package_id = p_package_id
       AND package.status = 'published'
       AND (
           package.created_by_user_id = p_actor_user_id
           OR EXISTS (
               SELECT 1
                 FROM public.admin_profiles AS profile
                WHERE profile.user_id = p_actor_user_id
                  AND profile.role IN ('owner', 'admin')
           )
       );

    IF NOT FOUND THEN
        RAISE EXCEPTION 'published package not found or withdrawal is not authorized';
    END IF;
END;
$function$;

REVOKE ALL ON FUNCTION public.resolve_deployment_package_publisher(bigint, text)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.publish_deployment_package(uuid, bigint, text, integer, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.search_deployment_packages(text, bigint, integer, integer)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.get_deployment_package_download(uuid)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.set_deployment_package_publisher(text, bigint, boolean, text)
    FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON FUNCTION public.withdraw_deployment_package(uuid, text, text)
    FROM PUBLIC, anon, authenticated, service_role;

GRANT EXECUTE ON FUNCTION public.resolve_deployment_package_publisher(bigint, text)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.publish_deployment_package(uuid, bigint, text, integer, text, text)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.search_deployment_packages(text, bigint, integer, integer)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.get_deployment_package_download(uuid)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.set_deployment_package_publisher(text, bigint, boolean, text)
    TO service_role;
GRANT EXECUTE ON FUNCTION public.withdraw_deployment_package(uuid, text, text)
    TO service_role;
