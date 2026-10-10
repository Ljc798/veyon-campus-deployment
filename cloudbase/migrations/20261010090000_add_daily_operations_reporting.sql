BEGIN;

CREATE TABLE public.campus_daily_operations_reports (
    day_hkt date NOT NULL,
    campus_identity_digest char(64) NOT NULL
        CHECK (campus_identity_digest ~ '^[A-F0-9]{64}$'),
    publisher_digest char(64) NOT NULL
        CHECK (publisher_digest ~ '^[A-F0-9]{64}$'),
    update_succeeded_count integer NOT NULL CHECK (update_succeeded_count BETWEEN 0 AND 20000),
    update_partial_count integer NOT NULL CHECK (update_partial_count BETWEEN 0 AND 20000),
    update_failed_count integer NOT NULL CHECK (update_failed_count BETWEEN 0 AND 20000),
    update_cancelled_count integer NOT NULL CHECK (update_cancelled_count BETWEEN 0 AND 20000),
    student_target_succeeded_count integer NOT NULL CHECK (student_target_succeeded_count BETWEEN 0 AND 20000),
    student_target_needs_review_count integer NOT NULL CHECK (student_target_needs_review_count BETWEEN 0 AND 20000),
    student_target_failed_count integer NOT NULL CHECK (student_target_failed_count BETWEEN 0 AND 20000),
    classroom_session_count smallint NOT NULL CHECK (classroom_session_count BETWEEN 0 AND 30),
    failure_counts jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(failure_counts) = 'object'),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (day_hkt, campus_identity_digest, publisher_digest)
);

COMMENT ON TABLE public.campus_daily_operations_reports IS
    'Optional daily Teacher aggregates. Campus and publisher identities are server-derived daily HMACs; no raw IDs or per-device diagnostics are stored.';
COMMENT ON COLUMN public.campus_daily_operations_reports.failure_counts IS
    'Counts of fixed UPDATE_* error codes only; arbitrary messages, paths, names and device-level records are never accepted.';

ALTER TABLE public.telemetry_hkt_retention_state
    ADD COLUMN last_operations_cleanup_hkt date;
UPDATE public.telemetry_hkt_retention_state
   SET last_operations_cleanup_hkt = (timezone('Asia/Hong_Kong', statement_timestamp()))::date - 1
 WHERE last_operations_cleanup_hkt IS NULL;
ALTER TABLE public.telemetry_hkt_retention_state
    ALTER COLUMN last_operations_cleanup_hkt SET NOT NULL;

CREATE FUNCTION public.record_campus_daily_operations_report_v1(
    p_day_hkt date,
    p_publisher_digest text,
    p_package_id uuid,
    p_campus_identity_digest text,
    p_update_succeeded_count integer,
    p_update_partial_count integer,
    p_update_failed_count integer,
    p_update_cancelled_count integer,
    p_student_target_succeeded_count integer,
    p_student_target_needs_review_count integer,
    p_student_target_failed_count integer,
    p_classroom_session_count integer,
    p_failure_counts jsonb
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_today_hkt date := (timezone('Asia/Hong_Kong', statement_timestamp()))::date;
    v_failure_key text;
    v_failure_value jsonb;
    v_failure_sum integer := 0;
    v_failure_count integer;
BEGIN
    IF p_day_hkt IS NULL OR p_day_hkt >= v_today_hkt OR p_day_hkt < v_today_hkt - 14 THEN
        RAISE EXCEPTION 'operations report date is outside the supported range';
    END IF;
    IF p_publisher_digest IS NULL OR p_publisher_digest !~ '^[A-F0-9]{64}$' OR
       p_campus_identity_digest IS NULL OR p_campus_identity_digest !~ '^[A-F0-9]{64}$' THEN
        RAISE EXCEPTION 'operations report identity digest is invalid';
    END IF;
    IF p_update_succeeded_count NOT BETWEEN 0 AND 20000 OR
       p_update_partial_count NOT BETWEEN 0 AND 20000 OR
       p_update_failed_count NOT BETWEEN 0 AND 20000 OR
       p_update_cancelled_count NOT BETWEEN 0 AND 20000 OR
       p_student_target_succeeded_count NOT BETWEEN 0 AND 20000 OR
       p_student_target_needs_review_count NOT BETWEEN 0 AND 20000 OR
       p_student_target_failed_count NOT BETWEEN 0 AND 20000 OR
       p_classroom_session_count NOT BETWEEN 0 AND 30 THEN
        RAISE EXCEPTION 'operations report count is outside the supported range';
    END IF;
    IF p_failure_counts IS NULL OR jsonb_typeof(p_failure_counts) <> 'object' OR
       (SELECT count(*) FROM jsonb_object_keys(p_failure_counts)) > 11 THEN
        RAISE EXCEPTION 'operations report failure counts are invalid';
    END IF;

    FOR v_failure_key, v_failure_value IN SELECT key, value FROM jsonb_each(p_failure_counts)
    LOOP
        IF v_failure_key NOT IN (
            'UPDATE_PARTIAL', 'UPDATE_TIMEOUT', 'UPDATE_NETWORK', 'UPDATE_PERMISSION_DENIED',
            'UPDATE_SIGNATURE_INVALID', 'UPDATE_ARTIFACT_INVALID', 'UPDATE_UNSUPPORTED',
            'UPDATE_CONFIGURATION', 'UPDATE_LOCAL_IO', 'UPDATE_INPUT_INVALID', 'UPDATE_UNKNOWN'
        ) OR jsonb_typeof(v_failure_value) <> 'number' OR
           v_failure_value::text !~ '^(0|[1-9][0-9]*)$' THEN
            RAISE EXCEPTION 'operations report failure category is invalid';
        END IF;
        v_failure_count := (v_failure_value::text)::integer;
        IF v_failure_count NOT BETWEEN 0 AND 128 THEN
            RAISE EXCEPTION 'operations report failure count is outside the supported range';
        END IF;
        v_failure_sum := v_failure_sum + v_failure_count;
    END LOOP;
    IF v_failure_sum > p_update_partial_count + p_update_failed_count THEN
        RAISE EXCEPTION 'operations report failure totals exceed update results';
    END IF;

    PERFORM 1
      FROM public.deployment_packages AS package
      LEFT JOIN public.campuses AS campus ON campus.id = package.campus_id
     WHERE package.package_id = p_package_id
       AND package.status = 'published'
       AND (package.campus_id IS NULL OR campus.status = 'active');
    IF NOT FOUND THEN
        RAISE EXCEPTION 'published package is not mapped to an active campus';
    END IF;

    INSERT INTO public.campus_daily_operations_reports (
        day_hkt, campus_identity_digest, publisher_digest,
        update_succeeded_count, update_partial_count, update_failed_count, update_cancelled_count,
        student_target_succeeded_count, student_target_needs_review_count, student_target_failed_count,
        classroom_session_count, failure_counts, updated_at
    ) VALUES (
        p_day_hkt, upper(p_campus_identity_digest), upper(p_publisher_digest),
        p_update_succeeded_count, p_update_partial_count, p_update_failed_count,
        p_update_cancelled_count, p_student_target_succeeded_count, p_student_target_needs_review_count,
        p_student_target_failed_count, p_classroom_session_count, p_failure_counts, now()
    )
    ON CONFLICT (day_hkt, campus_identity_digest, publisher_digest) DO UPDATE SET
        update_succeeded_count = EXCLUDED.update_succeeded_count,
        update_partial_count = EXCLUDED.update_partial_count,
        update_failed_count = EXCLUDED.update_failed_count,
        update_cancelled_count = EXCLUDED.update_cancelled_count,
        student_target_succeeded_count = EXCLUDED.student_target_succeeded_count,
        student_target_needs_review_count = EXCLUDED.student_target_needs_review_count,
        student_target_failed_count = EXCLUDED.student_target_failed_count,
        classroom_session_count = EXCLUDED.classroom_session_count,
        failure_counts = EXCLUDED.failure_counts,
        updated_at = now();

    UPDATE public.telemetry_hkt_retention_state
       SET last_operations_cleanup_hkt = v_today_hkt
     WHERE id = true AND last_operations_cleanup_hkt < v_today_hkt;
    IF FOUND THEN
        DELETE FROM public.campus_daily_operations_reports WHERE day_hkt < v_today_hkt - 400;
    END IF;
END;
$function$;

CREATE FUNCTION public.get_campus_daily_operations_summary_v1(p_days integer)
RETURNS TABLE (
    day_hkt date,
    reporting_campuses integer,
    update_succeeded bigint,
    update_partial bigint,
    update_failed bigint,
    update_cancelled bigint,
    student_target_succeeded bigint,
    student_target_needs_review bigint,
    student_target_failed bigint,
    classroom_sessions bigint,
    failure_counts jsonb
)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_today_hkt date := (timezone('Asia/Hong_Kong', statement_timestamp()))::date;
BEGIN
    IF p_days NOT IN (7, 30, 90) THEN
        RAISE EXCEPTION 'operations summary range is invalid';
    END IF;
    RETURN QUERY
    WITH filtered AS (
        SELECT report.*
          FROM public.campus_daily_operations_reports AS report
         WHERE report.day_hkt BETWEEN v_today_hkt - (p_days - 1) AND v_today_hkt - 1
    ), categories AS (
        SELECT report.day_hkt, category.key AS code, sum((category.value)::integer)::bigint AS count
          FROM filtered AS report
          CROSS JOIN LATERAL jsonb_each_text(report.failure_counts) AS category(key, value)
         GROUP BY report.day_hkt, category.key
    ), category_objects AS (
        SELECT categories.day_hkt,
               jsonb_object_agg(categories.code, categories.count ORDER BY categories.code) AS failure_counts
          FROM categories
         GROUP BY categories.day_hkt
    )
    SELECT report.day_hkt,
           count(DISTINCT report.campus_identity_digest)::integer,
           sum(report.update_succeeded_count)::bigint,
           sum(report.update_partial_count)::bigint,
           sum(report.update_failed_count)::bigint,
           sum(report.update_cancelled_count)::bigint,
           sum(report.student_target_succeeded_count)::bigint,
           sum(report.student_target_needs_review_count)::bigint,
           sum(report.student_target_failed_count)::bigint,
           sum(report.classroom_session_count)::bigint,
           coalesce(category_objects.failure_counts, '{}'::jsonb)
      FROM filtered AS report
      LEFT JOIN category_objects ON category_objects.day_hkt = report.day_hkt
     GROUP BY report.day_hkt, category_objects.failure_counts
     ORDER BY report.day_hkt;
END;
$function$;

REVOKE ALL ON TABLE public.campus_daily_operations_reports FROM PUBLIC, anon, authenticated, service_role;
GRANT ALL ON TABLE public.campus_daily_operations_reports TO service_role;
ALTER TABLE public.campus_daily_operations_reports ENABLE ROW LEVEL SECURITY;
CREATE POLICY campus_daily_operations_reports_service_only
    ON public.campus_daily_operations_reports FOR ALL TO service_role
    USING (true) WITH CHECK (true);

REVOKE ALL ON FUNCTION public.record_campus_daily_operations_report_v1(
    date, text, uuid, text, integer, integer, integer, integer, integer, integer, integer, integer, jsonb
) FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.record_campus_daily_operations_report_v1(
    date, text, uuid, text, integer, integer, integer, integer, integer, integer, integer, integer, jsonb
) TO service_role;

REVOKE ALL ON FUNCTION public.get_campus_daily_operations_summary_v1(integer)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.get_campus_daily_operations_summary_v1(integer) TO service_role;

COMMIT;
