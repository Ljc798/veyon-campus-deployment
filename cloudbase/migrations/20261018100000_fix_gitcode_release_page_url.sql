BEGIN;

ALTER TABLE public.site_download_settings
    DROP CONSTRAINT IF EXISTS site_download_settings_urls_match_source;

UPDATE public.site_download_settings
   SET release_url = 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases',
       updated_at = now()
 WHERE source = 'gitcode';

ALTER TABLE public.site_download_settings
    ADD CONSTRAINT site_download_settings_urls_match_source CHECK (
        tag = 'v' || version AND
        CASE source
            WHEN 'github' THEN
                release_url = 'https://github.com/Ljc798/veyon-campus-deployment/releases/tag/' || tag AND
                teacher_download_url = 'https://github.com/Ljc798/veyon-campus-deployment/releases/download/' || tag || '/VeyonCampus-Teacher-Setup-' || version || '-win-x64.exe' AND
                student_download_url = 'https://github.com/Ljc798/veyon-campus-deployment/releases/download/' || tag || '/VeyonCampus-Student-Setup-' || version || '-win-x64.exe'
            WHEN 'gitcode' THEN
                release_url = 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases' AND
                teacher_download_url = 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases/download/' || tag || '/VeyonCampus-Teacher-Setup-' || version || '-win-x64.exe' AND
                student_download_url = 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases/download/' || tag || '/VeyonCampus-Student-Setup-' || version || '-win-x64.exe'
            ELSE false
        END
    );

CREATE OR REPLACE FUNCTION public.save_site_download_settings_v1(
    p_github jsonb,
    p_gitcode jsonb,
    p_updated_by text
)
RETURNS SETOF public.site_download_settings
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    v_version text;
    v_tag text;
    v_release_url text;
    v_teacher_url text;
    v_student_url text;
BEGIN
    IF p_updated_by IS NULL OR length(p_updated_by) NOT BETWEEN 1 AND 128 OR p_updated_by ~ '[[:cntrl:]]' THEN
        RAISE EXCEPTION 'download settings actor is invalid';
    END IF;

    IF p_github IS NULL OR jsonb_typeof(p_github) <> 'object' OR
       p_gitcode IS NULL OR jsonb_typeof(p_gitcode) <> 'object' THEN
        RAISE EXCEPTION 'download settings payload is invalid';
    END IF;

    v_version := p_github ->> 'version';
    v_tag := p_github ->> 'tag';
    v_release_url := p_github ->> 'releaseUrl';
    v_teacher_url := p_github ->> 'teacherUrl';
    v_student_url := p_github ->> 'studentUrl';
    IF v_version IS NULL OR length(v_version) > 64 OR v_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' OR
       v_tag <> ('v' || v_version) OR
       v_release_url <> 'https://github.com/Ljc798/veyon-campus-deployment/releases/tag/' || v_tag OR
       v_teacher_url <> 'https://github.com/Ljc798/veyon-campus-deployment/releases/download/' || v_tag || '/VeyonCampus-Teacher-Setup-' || v_version || '-win-x64.exe' OR
       v_student_url <> 'https://github.com/Ljc798/veyon-campus-deployment/releases/download/' || v_tag || '/VeyonCampus-Student-Setup-' || v_version || '-win-x64.exe' THEN
        RAISE EXCEPTION 'GitHub download settings are invalid';
    END IF;
    INSERT INTO public.site_download_settings (
        source, version, tag, release_url, teacher_download_url, student_download_url, updated_at, updated_by
    ) VALUES (
        'github', v_version, v_tag, v_release_url, v_teacher_url, v_student_url, now(), p_updated_by
    ) ON CONFLICT (source) DO UPDATE SET
        version = EXCLUDED.version,
        tag = EXCLUDED.tag,
        release_url = EXCLUDED.release_url,
        teacher_download_url = EXCLUDED.teacher_download_url,
        student_download_url = EXCLUDED.student_download_url,
        updated_at = EXCLUDED.updated_at,
        updated_by = EXCLUDED.updated_by;

    v_version := p_gitcode ->> 'version';
    v_tag := p_gitcode ->> 'tag';
    v_release_url := p_gitcode ->> 'releaseUrl';
    v_teacher_url := p_gitcode ->> 'teacherUrl';
    v_student_url := p_gitcode ->> 'studentUrl';
    IF v_version IS NULL OR length(v_version) > 64 OR v_version !~ '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' OR
       v_tag <> ('v' || v_version) OR
       v_release_url <> 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases' OR
       v_teacher_url <> 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases/download/' || v_tag || '/VeyonCampus-Teacher-Setup-' || v_version || '-win-x64.exe' OR
       v_student_url <> 'https://gitcode.com/ljc_1126/veyon-campus-deployment/releases/download/' || v_tag || '/VeyonCampus-Student-Setup-' || v_version || '-win-x64.exe' THEN
        RAISE EXCEPTION 'GitCode download settings are invalid';
    END IF;
    INSERT INTO public.site_download_settings (
        source, version, tag, release_url, teacher_download_url, student_download_url, updated_at, updated_by
    ) VALUES (
        'gitcode', v_version, v_tag, v_release_url, v_teacher_url, v_student_url, now(), p_updated_by
    ) ON CONFLICT (source) DO UPDATE SET
        version = EXCLUDED.version,
        tag = EXCLUDED.tag,
        release_url = EXCLUDED.release_url,
        teacher_download_url = EXCLUDED.teacher_download_url,
        student_download_url = EXCLUDED.student_download_url,
        updated_at = EXCLUDED.updated_at,
        updated_by = EXCLUDED.updated_by;

    RETURN QUERY
    SELECT * FROM public.site_download_settings
     WHERE source IN ('github', 'gitcode')
     ORDER BY source;
END;
$function$;

REVOKE ALL ON FUNCTION public.save_site_download_settings_v1(jsonb, jsonb, text)
    FROM PUBLIC, anon, authenticated, service_role;
GRANT EXECUTE ON FUNCTION public.save_site_download_settings_v1(jsonb, jsonb, text)
    TO service_role;

COMMIT;
