#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${PGDATABASE:-}" ]]; then
    printf 'Set PGDATABASE to a disposable, empty PostgreSQL database before running this check.\n' >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
psql_flags=(-X -v ON_ERROR_STOP=1)
package_id='11111111-1111-4111-8111-111111111111'
temporary_directory="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/veyon-download-limit.XXXXXX")"
trap 'rm -rf -- "$temporary_directory"' EXIT

psql "${psql_flags[@]}" <<'SQL'
DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
        CREATE ROLE anon;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
        CREATE ROLE authenticated;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'service_role') THEN
        CREATE ROLE service_role;
    END IF;
END;
$roles$;
CREATE TABLE public.deployment_packages (
    package_id uuid PRIMARY KEY,
    status text NOT NULL,
    artifact_file_name text NOT NULL,
    artifact_size_bytes integer NOT NULL,
    artifact_sha256 text NOT NULL,
    publisher_phone_fingerprint text
);
CREATE TABLE public.deployment_package_artifacts (
    package_id uuid PRIMARY KEY REFERENCES public.deployment_packages(package_id),
    storage_key text NOT NULL
);
INSERT INTO public.deployment_packages VALUES (
    '11111111-1111-4111-8111-111111111111', 'published', 'config.zip', 128,
    repeat('A', 64), repeat('C', 64)
);
INSERT INTO public.deployment_package_artifacts VALUES (
    '11111111-1111-4111-8111-111111111111',
    'deployment-packages/v3/11111111-1111-4111-8111-111111111111.zip'
);
SQL

psql "${psql_flags[@]}" \
    --file "$repo_root/cloudbase/migrations/20261001090001_add_shared_package_download_rate_limit.sql" \
    >/dev/null

psql "${psql_flags[@]}" <<'SQL'
DO $verify$
DECLARE
    result record;
    attempt integer;
BEGIN
    FOR attempt IN 1..9 LOOP
        SELECT * INTO result FROM public.get_deployment_package_download_with_rate_limit(
            '11111111-1111-4111-8111-111111111111', repeat('B', 64), repeat('D', 64)
        );
        IF result.decision <> 'invalid' THEN
            RAISE EXCEPTION 'Expected invalid on attempt %, got %', attempt, result.decision;
        END IF;
    END LOOP;

    SELECT * INTO result FROM public.get_deployment_package_download_with_rate_limit(
        '11111111-1111-4111-8111-111111111111', repeat('B', 64), repeat('D', 64)
    );
    IF result.decision <> 'blocked' OR result.retry_after_seconds NOT BETWEEN 1 AND 900 THEN
        RAISE EXCEPTION 'Expected a bounded block, got % / %',
            result.decision, result.retry_after_seconds;
    END IF;

    SELECT * INTO result FROM public.get_deployment_package_download_with_rate_limit(
        '11111111-1111-4111-8111-111111111111', repeat('C', 64), repeat('D', 64)
    );
    IF result.decision <> 'blocked' THEN
        RAISE EXCEPTION 'Correct suffix bypassed an active lockout';
    END IF;

    SELECT * INTO result FROM public.get_deployment_package_download_with_rate_limit(
        '11111111-1111-4111-8111-111111111111', repeat('C', 64), repeat('E', 64)
    );
    IF result.decision <> 'authorized'
       OR result.storage_key <> 'deployment-packages/v3/11111111-1111-4111-8111-111111111111.zip' THEN
        RAISE EXCEPTION 'Independent client did not receive the authorized artifact';
    END IF;
END;
$verify$;
SQL

pids=()
for attempt in $(seq 1 10); do
    psql "${psql_flags[@]}" -At -c \
        "SELECT decision FROM public.get_deployment_package_download_with_rate_limit('$package_id', repeat('B', 64), repeat('F', 64));" \
        >"$temporary_directory/result.$attempt" 2>"$temporary_directory/error.$attempt" &
    pids+=("$!")
done

child_failure=0
set +e
for pid in "${pids[@]}"; do
    wait "$pid" || child_failure=1
done
set -e
if [[ "$child_failure" -ne 0 ]]; then
    cat "$temporary_directory"/error.* >&2
    exit 1
fi

invalid_count="$(cat "$temporary_directory"/result.* | awk '$0 == "invalid" { count++ } END { print count + 0 }')"
blocked_count="$(cat "$temporary_directory"/result.* | awk '$0 == "blocked" { count++ } END { print count + 0 }')"
if [[ "$invalid_count" -ne 9 || "$blocked_count" -ne 1 ]]; then
    printf 'Expected 9 invalid and 1 blocked concurrent response; got %s invalid and %s blocked.\n' \
        "$invalid_count" "$blocked_count" >&2
    exit 1
fi

lock_state="$(psql "${psql_flags[@]}" -At -F '|' -c \
    "SELECT failure_count, blocked_until > now() FROM public.deployment_package_download_attempts WHERE client_fingerprint = repeat('F', 64);")"
if [[ "$lock_state" != '10|t' ]]; then
    printf 'Concurrent failure state is unexpected: %s\n' "$lock_state" >&2
    exit 1
fi

if psql "${psql_flags[@]}" -At -c \
    "SET ROLE anon; SELECT decision FROM public.get_deployment_package_download_with_rate_limit('$package_id', repeat('C', 64), repeat('1', 64));" \
    >"$temporary_directory/anon.out" 2>"$temporary_directory/anon.err"; then
    printf 'anon unexpectedly executed the service-only RPC.\n' >&2
    exit 1
fi
if psql "${psql_flags[@]}" -At -c \
    'SET ROLE service_role; SELECT count(*) FROM public.deployment_package_download_attempts;' \
    >"$temporary_directory/service-role.out" 2>"$temporary_directory/service-role.err"; then
    printf 'service_role unexpectedly queried limiter state directly.\n' >&2
    exit 1
fi

printf 'PostgreSQL migration and shared download rate-limit checks passed.\n'
