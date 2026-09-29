-- Add UTC+8 heartbeat reporting beside the existing UTC tables and RPC. Keeping
-- the old names/contract lets an older static site or service finish a rollout
-- without breaking while the new client and server versions are deployed.

CREATE TABLE public.telemetry_daily_hkt_devices (
    day_hkt date NOT NULL,
    installation_digest char(64) NOT NULL
        CHECK (installation_digest ~ '^[A-F0-9]{64}$'),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (day_hkt, installation_digest)
);

CREATE TABLE public.telemetry_daily_hkt_stats (
    day_hkt date PRIMARY KEY,
    unique_devices integer NOT NULL DEFAULT 0 CHECK (unique_devices >= 0),
    heartbeat_signals bigint NOT NULL DEFAULT 0 CHECK (heartbeat_signals >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE public.telemetry_hkt_retention_state (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    last_cleanup_hkt date NOT NULL
);

INSERT INTO public.telemetry_hkt_retention_state (id, last_cleanup_hkt)
VALUES (true, (timezone('Asia/Hong_Kong', now()))::date - 1);

CREATE TABLE public.telemetry_daily_deployment_devices (
    day_hkt date NOT NULL,
    campus_id bigint NOT NULL REFERENCES public.campuses(id) ON DELETE RESTRICT,
    deployment_id uuid NOT NULL REFERENCES public.deployment_packages(package_id) ON DELETE RESTRICT,
    application_version varchar(64) NOT NULL
        CHECK (application_version = 'unknown' OR
               application_version ~ '^[0-9]+(\.[0-9]+){2,3}([-+][0-9A-Za-z.-]+)?$'),
    installation_digest char(64) NOT NULL
        CHECK (installation_digest ~ '^[A-F0-9]{64}$'),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (day_hkt, campus_id, deployment_id, application_version, installation_digest)
);

CREATE TABLE public.telemetry_daily_deployment_stats (
    day_hkt date NOT NULL,
    campus_id bigint NOT NULL REFERENCES public.campuses(id) ON DELETE RESTRICT,
    deployment_id uuid NOT NULL REFERENCES public.deployment_packages(package_id) ON DELETE RESTRICT,
    application_version varchar(64) NOT NULL
        CHECK (application_version = 'unknown' OR
               application_version ~ '^[0-9]+(\.[0-9]+){2,3}([-+][0-9A-Za-z.-]+)?$'),
    unique_devices integer NOT NULL DEFAULT 0 CHECK (unique_devices >= 0),
    heartbeat_signals bigint NOT NULL DEFAULT 0 CHECK (heartbeat_signals >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (day_hkt, campus_id, deployment_id, application_version)
);

CREATE INDEX telemetry_daily_deployment_stats_campus_day_idx
    ON public.telemetry_daily_deployment_stats (campus_id, day_hkt DESC);

COMMENT ON TABLE public.telemetry_daily_deployment_devices IS
    'Daily HMAC digests scoped to a published deployment package; raw installation IDs are never stored.';
COMMENT ON TABLE public.telemetry_daily_deployment_stats IS
    'Daily anonymous usage counts by registered campus, published deployment package, and StudentSetup version.';
COMMENT ON COLUMN public.telemetry_daily_deployment_devices.installation_digest IS
    'Daily HMAC digest scoped to the deployment package so different campuses/packages cannot correlate installations.';

CREATE FUNCTION public.record_telemetry_heartbeat_v2(
    p_day_hkt date,
    p_installation_digest text,
    p_deployment_digest text,
    p_application_version text,
    p_deployment_id uuid
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_inserted integer;
    v_campus_id bigint;
BEGIN
    IF p_day_hkt IS DISTINCT FROM (timezone('Asia/Hong_Kong', statement_timestamp()))::date THEN
        RAISE EXCEPTION 'heartbeat date must be the current Asia/Hong_Kong date';
    END IF;
    IF p_installation_digest !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'invalid installation digest';
    END IF;
    IF p_application_version IS NULL OR length(p_application_version) > 64 OR
       (p_application_version <> 'unknown' AND
        p_application_version !~ '^[0-9]+(\.[0-9]+){2,3}([-+][0-9A-Za-z.-]+)?$') THEN
        RAISE EXCEPTION 'invalid application version';
    END IF;
    IF (p_deployment_id IS NULL) <> (p_deployment_digest IS NULL) THEN
        RAISE EXCEPTION 'deployment identifier and digest must be supplied together';
    END IF;
    IF p_deployment_digest IS NOT NULL AND p_deployment_digest !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'invalid deployment digest';
    END IF;

    INSERT INTO public.telemetry_daily_hkt_devices (day_hkt, installation_digest)
    VALUES (p_day_hkt, p_installation_digest)
    ON CONFLICT (day_hkt, installation_digest) DO NOTHING;
    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    INSERT INTO public.telemetry_daily_hkt_stats (
        day_hkt, unique_devices, heartbeat_signals, updated_at
    ) VALUES (
        p_day_hkt, v_inserted, 1, now()
    )
    ON CONFLICT (day_hkt) DO UPDATE SET
        unique_devices = public.telemetry_daily_hkt_stats.unique_devices + EXCLUDED.unique_devices,
        heartbeat_signals = public.telemetry_daily_hkt_stats.heartbeat_signals + 1,
        updated_at = now();

    IF p_deployment_id IS NOT NULL THEN
        SELECT package.campus_id
          INTO v_campus_id
          FROM public.deployment_packages AS package
         WHERE package.package_id = p_deployment_id;

        IF FOUND AND v_campus_id IS NOT NULL THEN
            INSERT INTO public.telemetry_daily_deployment_devices (
                day_hkt, campus_id, deployment_id, application_version, installation_digest
            ) VALUES (
                p_day_hkt, v_campus_id, p_deployment_id, p_application_version, p_deployment_digest
            )
            ON CONFLICT (day_hkt, campus_id, deployment_id, application_version, installation_digest)
                DO NOTHING;
            GET DIAGNOSTICS v_inserted = ROW_COUNT;

            INSERT INTO public.telemetry_daily_deployment_stats (
                day_hkt, campus_id, deployment_id, application_version,
                unique_devices, heartbeat_signals, updated_at
            ) VALUES (
                p_day_hkt, v_campus_id, p_deployment_id, p_application_version,
                v_inserted, 1, now()
            )
            ON CONFLICT (day_hkt, campus_id, deployment_id, application_version) DO UPDATE SET
                unique_devices = public.telemetry_daily_deployment_stats.unique_devices + EXCLUDED.unique_devices,
                heartbeat_signals = public.telemetry_daily_deployment_stats.heartbeat_signals + 1,
                updated_at = now();
        END IF;
    END IF;

    UPDATE public.telemetry_hkt_retention_state
       SET last_cleanup_hkt = p_day_hkt
     WHERE id = true AND last_cleanup_hkt < p_day_hkt;

    IF FOUND THEN
        DELETE FROM public.telemetry_daily_hkt_devices WHERE day_hkt < p_day_hkt - 90;
        DELETE FROM public.telemetry_daily_deployment_devices WHERE day_hkt < p_day_hkt - 90;
        DELETE FROM public.telemetry_daily_hkt_stats WHERE day_hkt < p_day_hkt - 400;
        DELETE FROM public.telemetry_daily_deployment_stats WHERE day_hkt < p_day_hkt - 400;
    END IF;
END;
$function$;

REVOKE ALL ON FUNCTION public.record_telemetry_heartbeat_v2(date, text, text, text, uuid)
    FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.record_telemetry_heartbeat_v2(date, text, text, text, uuid)
    TO service_role;

REVOKE ALL ON TABLE public.telemetry_daily_hkt_devices FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON TABLE public.telemetry_daily_hkt_stats FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON TABLE public.telemetry_hkt_retention_state FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON TABLE public.telemetry_daily_deployment_devices FROM PUBLIC, anon, authenticated, service_role;
REVOKE ALL ON TABLE public.telemetry_daily_deployment_stats FROM PUBLIC, anon, authenticated, service_role;
GRANT ALL ON TABLE public.telemetry_daily_hkt_devices TO service_role;
GRANT ALL ON TABLE public.telemetry_daily_hkt_stats TO service_role;
GRANT ALL ON TABLE public.telemetry_hkt_retention_state TO service_role;
GRANT ALL ON TABLE public.telemetry_daily_deployment_devices TO service_role;
GRANT ALL ON TABLE public.telemetry_daily_deployment_stats TO service_role;
GRANT SELECT ON TABLE public.telemetry_daily_hkt_stats TO authenticated;
GRANT SELECT ON TABLE public.telemetry_daily_deployment_stats TO authenticated;

ALTER TABLE public.telemetry_daily_hkt_devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.telemetry_daily_hkt_stats ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.telemetry_hkt_retention_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.telemetry_daily_deployment_devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.telemetry_daily_deployment_stats ENABLE ROW LEVEL SECURITY;

CREATE POLICY telemetry_daily_hkt_stats_read_site_admins
    ON public.telemetry_daily_hkt_stats
    FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_profiles AS profile
            WHERE profile.user_id = (SELECT auth.uid())
              AND profile.role IN ('owner', 'admin', 'editor', 'viewer')
        )
    );

CREATE POLICY telemetry_daily_hkt_devices_service_only
    ON public.telemetry_daily_hkt_devices
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);

CREATE POLICY telemetry_hkt_retention_state_service_only
    ON public.telemetry_hkt_retention_state
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);

CREATE POLICY telemetry_daily_deployment_stats_read_site_admins
    ON public.telemetry_daily_deployment_stats
    FOR SELECT TO authenticated
    USING (
        EXISTS (
            SELECT 1 FROM public.admin_profiles AS profile
            WHERE profile.user_id = (SELECT auth.uid())
              AND profile.role IN ('owner', 'admin', 'editor', 'viewer')
        )
    );

CREATE POLICY telemetry_daily_deployment_devices_service_only
    ON public.telemetry_daily_deployment_devices
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);

CREATE POLICY telemetry_daily_deployment_stats_service_write
    ON public.telemetry_daily_deployment_stats
    FOR ALL TO service_role
    USING (true)
    WITH CHECK (true);
