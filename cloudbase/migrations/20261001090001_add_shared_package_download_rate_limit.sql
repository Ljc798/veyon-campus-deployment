CREATE TABLE public.deployment_package_download_attempts (
    package_id uuid NOT NULL REFERENCES public.deployment_packages(package_id) ON DELETE CASCADE,
    client_fingerprint text NOT NULL CHECK (client_fingerprint ~ '^[A-F0-9]{64}$'),
    window_started_at timestamptz NOT NULL,
    failure_count smallint NOT NULL CHECK (failure_count BETWEEN 0 AND 10),
    blocked_until timestamptz,
    last_attempt_at timestamptz NOT NULL,
    CONSTRAINT deployment_package_download_attempts_pk
        PRIMARY KEY (package_id, client_fingerprint)
);

CREATE INDEX deployment_package_download_attempts_last_attempt_idx
    ON public.deployment_package_download_attempts (last_attempt_at);

ALTER TABLE public.deployment_package_download_attempts ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.deployment_package_download_attempts
    FROM PUBLIC, anon, authenticated, service_role;

COMMENT ON TABLE public.deployment_package_download_attempts IS
    'Stores HMAC fingerprints of client addresses and failed download attempts; raw IP addresses are never persisted.';

CREATE FUNCTION public.get_deployment_package_download_with_rate_limit(
    p_package_id uuid,
    p_phone_fingerprint text,
    p_client_fingerprint text
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
    v_now timestamptz := clock_timestamp();
    v_package_id uuid;
    v_storage_key text;
    v_artifact_file_name text;
    v_artifact_size_bytes integer;
    v_artifact_sha256 text;
    v_phone_fingerprint text;
    v_window_started_at timestamptz;
    v_failure_count integer;
    v_blocked_until timestamptz;
    v_retry_after_seconds integer;
BEGIN
    IF p_package_id IS NULL
       OR p_phone_fingerprint IS NULL
       OR p_phone_fingerprint !~ '^[A-F0-9]{64}$'
       OR p_client_fingerprint IS NULL
       OR p_client_fingerprint !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'download verification request is invalid';
    END IF;

    SELECT package.package_id,
           artifact.storage_key,
           package.artifact_file_name,
           package.artifact_size_bytes,
           package.artifact_sha256,
           package.publisher_phone_fingerprint
      INTO v_package_id,
           v_storage_key,
           v_artifact_file_name,
           v_artifact_size_bytes,
           v_artifact_sha256,
           v_phone_fingerprint
      FROM public.deployment_packages AS package
      JOIN public.deployment_package_artifacts AS artifact
        ON artifact.package_id = package.package_id
     WHERE package.package_id = p_package_id
       AND package.status = 'published'
     FOR SHARE OF package;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'not_found'::text, 0::integer, NULL::uuid, NULL::text,
                            NULL::text, NULL::integer, NULL::text;
        RETURN;
    END IF;

    IF random() < 0.01 THEN
        DELETE FROM public.deployment_package_download_attempts AS attempts
         WHERE attempts.ctid IN (
             SELECT expired.ctid
               FROM public.deployment_package_download_attempts AS expired
              WHERE expired.last_attempt_at < v_now - interval '1 day'
              ORDER BY expired.last_attempt_at
              LIMIT 100
         );
    END IF;

    INSERT INTO public.deployment_package_download_attempts (
        package_id,
        client_fingerprint,
        window_started_at,
        failure_count,
        last_attempt_at
    ) VALUES (
        p_package_id,
        p_client_fingerprint,
        v_now,
        0,
        v_now
    )
    ON CONFLICT ON CONSTRAINT deployment_package_download_attempts_pk DO NOTHING;

    SELECT attempts.window_started_at,
           attempts.failure_count,
           attempts.blocked_until
      INTO v_window_started_at,
           v_failure_count,
           v_blocked_until
      FROM public.deployment_package_download_attempts AS attempts
     WHERE attempts.package_id = p_package_id
       AND attempts.client_fingerprint = p_client_fingerprint
     FOR UPDATE;

    IF v_blocked_until IS NOT NULL AND v_blocked_until > v_now THEN
        v_retry_after_seconds := greatest(
            1,
            ceil(extract(epoch FROM (v_blocked_until - v_now)))::integer
        );
        RETURN QUERY SELECT 'blocked'::text, v_retry_after_seconds, NULL::uuid, NULL::text,
                            NULL::text, NULL::integer, NULL::text;
        RETURN;
    END IF;

    IF v_window_started_at <= v_now - interval '15 minutes' THEN
        v_window_started_at := v_now;
        v_failure_count := 0;
        v_blocked_until := NULL;
        UPDATE public.deployment_package_download_attempts AS attempts
           SET window_started_at = v_window_started_at,
               failure_count = 0,
               blocked_until = NULL,
               last_attempt_at = v_now
         WHERE attempts.package_id = p_package_id
           AND attempts.client_fingerprint = p_client_fingerprint;
    END IF;

    IF p_phone_fingerprint = v_phone_fingerprint THEN
        UPDATE public.deployment_package_download_attempts AS attempts
           SET window_started_at = v_now,
               failure_count = 0,
               blocked_until = NULL,
               last_attempt_at = v_now
         WHERE attempts.package_id = p_package_id
           AND attempts.client_fingerprint = p_client_fingerprint;
        RETURN QUERY SELECT 'authorized'::text, 0::integer, v_package_id, v_storage_key,
                            v_artifact_file_name, v_artifact_size_bytes, v_artifact_sha256;
        RETURN;
    END IF;

    v_failure_count := v_failure_count + 1;
    IF v_failure_count >= 10 THEN
        v_blocked_until := v_now + interval '15 minutes';
        v_retry_after_seconds := greatest(
            1,
            ceil(extract(epoch FROM (v_blocked_until - v_now)))::integer
        );
        UPDATE public.deployment_package_download_attempts AS attempts
           SET failure_count = v_failure_count,
               blocked_until = v_blocked_until,
               last_attempt_at = v_now
         WHERE attempts.package_id = p_package_id
           AND attempts.client_fingerprint = p_client_fingerprint;
        RETURN QUERY SELECT 'blocked'::text, v_retry_after_seconds, NULL::uuid, NULL::text,
                            NULL::text, NULL::integer, NULL::text;
        RETURN;
    END IF;

    UPDATE public.deployment_package_download_attempts AS attempts
       SET failure_count = v_failure_count,
           blocked_until = NULL,
           last_attempt_at = v_now
     WHERE attempts.package_id = p_package_id
       AND attempts.client_fingerprint = p_client_fingerprint;

    RETURN QUERY SELECT 'invalid'::text, 0::integer, NULL::uuid, NULL::text,
                        NULL::text, NULL::integer, NULL::text;
END;
$function$;

REVOKE ALL ON FUNCTION public.get_deployment_package_download_with_rate_limit(uuid, text, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.get_deployment_package_download_with_rate_limit(uuid, text, text)
    TO service_role;
