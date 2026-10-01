-- Remove the superseded UTC telemetry schema and the unused authenticated
-- teacher-to-campus publisher assignments. Active heartbeat and publication
-- flows use the UTC+8 telemetry and anonymous package publication APIs.

DROP FUNCTION IF EXISTS public.record_telemetry_heartbeat(date, text);
DROP FUNCTION IF EXISTS public.resolve_deployment_package_publisher(bigint, text);
DROP FUNCTION IF EXISTS public.set_deployment_package_publisher(text, bigint, boolean, text);

DROP TABLE IF EXISTS public.deployment_package_publishers;
DROP TABLE IF EXISTS public.telemetry_daily_devices;
DROP TABLE IF EXISTS public.telemetry_daily_stats;
DROP TABLE IF EXISTS public.telemetry_retention_state;
