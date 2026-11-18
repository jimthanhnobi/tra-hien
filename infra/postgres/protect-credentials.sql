-- Session-only protection: never ALTER SYSTEM/DATABASE/ROLE logging policy.
-- Run after EVERY connect, before getenv/interpolation of any credential.
-- ON_ERROR_STOP in the caller makes SET or verification failure fail closed.
SET log_min_messages = 'panic';
SET log_statement = 'none';
SET log_duration = off;
SET log_min_duration_statement = -1;
SET log_min_duration_sample = -1;
SET log_statement_sample_rate = 0;
SET log_transaction_sample_rate = 0;
SET log_min_error_statement = 'panic';
SET log_parameter_max_length = 0;
SET log_parameter_max_length_on_error = 0;
SET debug_print_parse = off;
SET debug_print_rewritten = off;
SET debug_print_plan = off;
DO $credential_logging$
BEGIN
  IF EXISTS (
    SELECT FROM (VALUES
      ('log_min_messages','panic'), ('log_statement','none'), ('log_duration','off'),
      ('log_min_duration_statement','-1'), ('log_min_duration_sample','-1'),
      ('log_statement_sample_rate','0'), ('log_transaction_sample_rate','0'),
      ('log_min_error_statement','panic'), ('log_parameter_max_length','0'),
      ('log_parameter_max_length_on_error','0'), ('debug_print_parse','off'),
      ('debug_print_rewritten','off'), ('debug_print_plan','off')
    ) AS expected(name,value)
    LEFT JOIN pg_settings actual ON actual.name=expected.name
    WHERE actual.setting IS DISTINCT FROM expected.value
  ) THEN
    RAISE EXCEPTION 'Credential logging protection could not be verified';
  END IF;
END
$credential_logging$;
