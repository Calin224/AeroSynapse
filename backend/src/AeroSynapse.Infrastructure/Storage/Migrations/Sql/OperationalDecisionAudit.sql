-- Frozen migration payload. Audit never retains arbitrary input strings/secrets.
CREATE FUNCTION twin.audit_safe(value jsonb, field text DEFAULT '') RETURNS jsonb
LANGUAGE plpgsql IMMUTABLE SET search_path = pg_catalog, twin, pg_temp AS $$
DECLARE result jsonb; item record; text_value text;
BEGIN
  IF value IS NULL THEN RETURN 'null'::jsonb; END IF;
  IF lower(field) ~ '(password|passwd|secret|token|credential|authorization|cookie|api.?key|connection.?string)' THEN
    RETURN '"[REDACTED]"'::jsonb;
  END IF;
  IF jsonb_typeof(value) = 'object' THEN
    result := '{}'::jsonb;
    FOR item IN SELECT * FROM jsonb_each(value) LOOP
      IF lower(field) = 'payload' AND item.key NOT IN ('version','eventVersion','passengerCount','windowMinutes','share','staffCount','delayMinutes','isSynthetic') THEN
        result := result || jsonb_build_object('_redactedProperties',true);
        CONTINUE;
      END IF;
      result := result || jsonb_build_object(item.key, twin.audit_safe(item.value, item.key));
    END LOOP;
    RETURN result;
  ELSIF jsonb_typeof(value) = 'array' THEN
    SELECT coalesce(jsonb_agg(twin.audit_safe(v, field) ORDER BY ordinal), '[]'::jsonb) INTO result
      FROM jsonb_array_elements(value) WITH ORDINALITY AS items(v, ordinal);
    RETURN result;
  ELSIF jsonb_typeof(value) <> 'string' THEN RETURN value;
  END IF;
  text_value := value #>> '{}';
  IF lower(field) ~ 'json$' THEN
    BEGIN RETURN twin.audit_safe(text_value::jsonb, left(field, length(field)-4));
    EXCEPTION WHEN invalid_text_representation THEN RETURN '"[REDACTED]"'::jsonb; END;
  END IF;
  -- IDs, clocks, immutable hashes and closed enums are useful evidence. Other
  -- strings are deliberately omitted rather than attempting to guess secrets.
  IF text_value ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    OR (lower(field) ~ '(utc|timestamp)$' AND text_value ~ '^\d{4}-\d\d-\d\dT\d\d:\d\d:')
    OR (lower(field) IN ('inputhash','resulthash','enginebuild','contenthash') AND text_value ~ '^[0-9a-f]{64}$')
    OR (lower(field) IN ('status','action') AND text_value IN ('planned','pending','running','completed','failed','cancelled','approved','rejected','created'))
    OR (lower(field) = 'type' AND text_value IN ('passenger_surge','checkpoint_unavailable','checkpoint_available','staff_change','flight_delay','resume'))
  THEN RETURN value; END IF;
  RETURN '"[REDACTED]"'::jsonb;
END $$;

CREATE FUNCTION twin.audit_capture() RETURNS trigger LANGUAGE plpgsql
SET search_path = pg_catalog, twin, pg_temp AS $$
DECLARE
  before_row jsonb := CASE WHEN TG_OP = 'INSERT' THEN '{}'::jsonb ELSE to_jsonb(OLD) END;
  after_row jsonb := CASE WHEN TG_OP = 'DELETE' THEN '{}'::jsonb ELSE to_jsonb(NEW) END;
  row_data jsonb; old_safe jsonb; new_safe jsonb; changes jsonb; changed_fields jsonb;
  resource uuid; scenario uuid; run_id uuid; recommendation uuid; source_id uuid;
  version_before int; version_after int; virtual_minute double precision;
  actor_id text; actor_name text; correlation text; source_correlation text;
  previous_context record; event_item jsonb; configuration jsonb; old_configuration jsonb;
BEGIN
  IF TG_OP = 'UPDATE' AND before_row = after_row THEN RETURN NEW; END IF;
  row_data := CASE WHEN TG_OP = 'DELETE' THEN before_row ELSE after_row END;
  resource := (row_data->>'Id')::uuid;
  run_id := coalesce((row_data->>'RunId')::uuid, (row_data->>'SimulationRunId')::uuid, (row_data->>'BaselineRunId')::uuid);
  scenario := (row_data->>'ScenarioId')::uuid;
  recommendation := (row_data->>'RecommendationId')::uuid;
  IF TG_TABLE_NAME = 'scenarios' THEN scenario := resource; END IF;
  IF TG_TABLE_NAME = 'runs' THEN run_id := resource; END IF;
  IF TG_TABLE_NAME IN ('run_recommendations','recommendation_reviews') THEN recommendation := resource; END IF;
  IF recommendation IS NOT NULL AND scenario IS NULL THEN
    SELECT r."ScenarioId" INTO scenario FROM twin.recommendation_reviews r WHERE r."Id" = recommendation;
    version_before := (row_data->>'ScenarioRevisionBefore')::int;
    version_after := coalesce((row_data->>'ScenarioRevisionAfter')::int, (row_data->>'ScenarioRevision')::int);
  END IF;
  IF run_id IS NOT NULL THEN
    SELECT coalesce(scenario, r."ScenarioId"), r."ScenarioRevision" INTO scenario, version_after FROM twin.runs r WHERE r."Id" = run_id;
    IF scenario IS NULL THEN SELECT a."ScenarioId", a."ScenarioVersionAfter" INTO scenario, version_after
      FROM twin.audit_events a WHERE a."RunId" = run_id ORDER BY a."Sequence" LIMIT 1; END IF;
  END IF;
  IF TG_TABLE_NAME = 'scenarios' THEN
    version_before := (before_row->>'Revision')::int; version_after := (after_row->>'Revision')::int;
    configuration := coalesce(after_row->'ConfigurationJson', before_row->'ConfigurationJson');
    old_configuration := before_row->'ConfigurationJson';
    virtual_minute := 0; -- Configuration changes occur before the next run.
    FOR event_item IN SELECT v FROM jsonb_array_elements(coalesce(configuration->'events','[]'::jsonb)) v LOOP
      IF NOT EXISTS (SELECT 1 FROM jsonb_array_elements(coalesce(old_configuration->'events','[]'::jsonb)) old_event WHERE old_event = event_item) THEN
        virtual_minute := extract(epoch FROM ((event_item->>'occurredAtUtc')::timestamptz - (configuration->>'startsAtUtc')::timestamptz))/60;
        EXIT;
      END IF;
    END LOOP;
  ELSIF scenario IS NOT NULL AND version_after IS NULL THEN
    SELECT s."Revision" INTO version_after FROM twin.scenarios s WHERE s."Id" = scenario;
  END IF;
  IF TG_TABLE_NAME = 'runs' THEN
    -- AFTER DELETE cannot look the run up again. Keep its original reference even
    -- when it predates this journal or its earlier audit has already expired.
    scenario := (row_data->>'ScenarioId')::uuid;
    version_after := (row_data->>'ScenarioRevision')::int;
    IF row_data->>'Status' = 'completed' THEN
      virtual_minute := (row_data#>>'{InputSnapshotJson,scenario,inputs,durationMinutes}')::double precision;
    ELSE SELECT p."SimulationMinute" INTO virtual_minute FROM twin.run_progress p WHERE p."RunId" = resource; END IF;
    IF virtual_minute IS NULL AND TG_OP = 'INSERT' THEN virtual_minute := 0; END IF;
  END IF;
  actor_id := coalesce(nullif(current_setting('aerosynapse.actor_id',true),''),'system');
  actor_name := coalesce(nullif(current_setting('aerosynapse.actor',true),''),'system');
  correlation := nullif(current_setting('aerosynapse.correlation',true),'');
  source_id := coalesce(run_id, (row_data->>'BatchId')::uuid, resource);
  IF correlation IS NULL THEN
    SELECT a."CorrelationId" INTO source_correlation FROM twin.audit_events a
      WHERE a."ResourceId" = source_id AND a."ActorId" <> 'system' ORDER BY a."Sequence" LIMIT 1;
    correlation := coalesce(source_correlation, 'system:' || txid_current()::text);
  END IF;
  old_safe := twin.audit_safe(before_row); new_safe := twin.audit_safe(after_row);
  -- Compare original values so a changed redacted text field is still visible.
  SELECT coalesce(jsonb_agg(key ORDER BY key), '[]'::jsonb) INTO changed_fields
    FROM (SELECT key FROM jsonb_object_keys(before_row || after_row) key
      WHERE before_row->key IS DISTINCT FROM after_row->key) fields;
  changes := jsonb_build_object('fields',changed_fields,'before',old_safe,'after',new_safe);
  INSERT INTO twin.audit_events ("Id","ActorId","Actor","CorrelationId","Action","ResourceType","ResourceId",
    "ScenarioId","RunId","RecommendationId","ScenarioVersionBefore","ScenarioVersionAfter","RecordedAtUtc","VirtualMinute","IsSynthetic","ChangesJson")
  VALUES (gen_random_uuid(),left(actor_id,256),left(actor_name,256),left(correlation,64), TG_TABLE_NAME || '.' || lower(TG_OP),TG_TABLE_NAME,resource,
    scenario,run_id,recommendation,version_before,version_after,clock_timestamp(),virtual_minute,coalesce((row_data->>'IsSynthetic')::boolean,false),changes);
  RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

DO $$ DECLARE table_name text; BEGIN
  FOREACH table_name IN ARRAY ARRAY['layout_versions','scenarios','runs','simulation_commands','simulation_batches','batch_variants',
    'batch_replications','run_recommendations','recommendation_candidates','recommendation_reviews','recommendation_audit','events',
    'operational_notifications','notification_activity','anomaly_reviews','copilot_workflows'] LOOP
    EXECUTE format('CREATE TRIGGER decision_audit AFTER INSERT OR UPDATE OR DELETE ON twin.%I FOR EACH ROW EXECUTE FUNCTION twin.audit_capture()',table_name);
  END LOOP;
END $$;

CREATE FUNCTION twin.audit_immutable() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, twin, pg_temp AS $$
BEGIN
  IF TG_OP = 'DELETE' AND current_setting('aerosynapse.audit_cleanup',true) = 'retention'
    AND OLD."RecordedAtUtc" < clock_timestamp() - interval '7 days' THEN RETURN OLD; END IF;
  RAISE EXCEPTION 'Audit records are append-only; use the restricted retention procedure.' USING ERRCODE = '42501';
END $$;
CREATE TRIGGER audit_append_only BEFORE UPDATE OR DELETE ON twin.audit_events FOR EACH ROW EXECUTE FUNCTION twin.audit_immutable();
CREATE FUNCTION twin.audit_no_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Audit records cannot be truncated.' USING ERRCODE = '42501'; END $$;
CREATE TRIGGER audit_no_truncate BEFORE TRUNCATE ON twin.audit_events EXECUTE FUNCTION twin.audit_no_truncate();

CREATE FUNCTION twin.audit_expire(retention_days integer) RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, twin, pg_temp AS $$
DECLARE removed bigint;
BEGIN
  IF retention_days NOT BETWEEN 7 AND 3650 THEN RAISE EXCEPTION 'Audit retention must be 7-3650 days.'; END IF;
  PERFORM set_config('aerosynapse.audit_cleanup','retention',true);
  DELETE FROM twin.audit_events WHERE "RecordedAtUtc" < clock_timestamp() - make_interval(days => retention_days);
  GET DIAGNOSTICS removed = ROW_COUNT;
  PERFORM set_config('aerosynapse.audit_cleanup','',true);
  RETURN removed;
END $$;
REVOKE ALL ON FUNCTION twin.audit_expire(integer) FROM PUBLIC;
