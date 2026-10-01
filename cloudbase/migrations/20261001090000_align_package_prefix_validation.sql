BEGIN;

ALTER TABLE public.deployment_packages
    DROP CONSTRAINT IF EXISTS deployment_packages_computer_prefix_check,
    ADD CONSTRAINT deployment_packages_computer_prefix_check
        CHECK (
            computer_prefix ~ '^[A-Za-z0-9][A-Za-z0-9-]{0,11}$'
            AND computer_prefix ~ '[A-Za-z]'
        );

COMMIT;
