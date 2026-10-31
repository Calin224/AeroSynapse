CREATE FUNCTION twin.ai_evidence_immutable() RETURNS trigger LANGUAGE plpgsql
SET search_path = pg_catalog, twin, pg_temp AS $$
BEGIN
  IF TG_TABLE_NAME = 'ai_agent_events' AND TG_OP = 'DELETE'
    AND current_setting('aerosynapse.ai_cleanup',true) = 'retention'
    AND OLD."RecordedAtUtc" < clock_timestamp() - interval '7 days' THEN RETURN OLD; END IF;
  RAISE EXCEPTION 'AI governance evidence is append-only.' USING ERRCODE = '42501';
END $$;
CREATE TRIGGER ai_policy_immutable BEFORE UPDATE OR DELETE ON twin.ai_policies
FOR EACH ROW EXECUTE FUNCTION twin.ai_evidence_immutable();
CREATE TRIGGER ai_policy_no_truncate BEFORE TRUNCATE ON twin.ai_policies EXECUTE FUNCTION twin.audit_no_truncate();
CREATE TRIGGER ai_event_immutable BEFORE UPDATE OR DELETE ON twin.ai_agent_events
FOR EACH ROW EXECUTE FUNCTION twin.ai_evidence_immutable();
CREATE TRIGGER ai_event_no_truncate BEFORE TRUNCATE ON twin.ai_agent_events EXECUTE FUNCTION twin.audit_no_truncate();
CREATE TRIGGER decision_audit AFTER INSERT ON twin.ai_policies FOR EACH ROW EXECUTE FUNCTION twin.audit_capture();

CREATE FUNCTION twin.ai_agent_expire(retention_days integer) RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, twin, pg_temp AS $$
DECLARE removed bigint;
BEGIN
  IF retention_days NOT BETWEEN 7 AND 3650 THEN RAISE EXCEPTION 'Retention must be 7-3650 days.'; END IF;
  PERFORM set_config('aerosynapse.ai_cleanup','retention',true);
  DELETE FROM twin.ai_agent_events WHERE "RecordedAtUtc" < clock_timestamp() - make_interval(days => retention_days);
  GET DIAGNOSTICS removed = ROW_COUNT;
  PERFORM set_config('aerosynapse.ai_cleanup','',true);
  RETURN removed;
END $$;
REVOKE ALL ON FUNCTION twin.ai_agent_expire(integer) FROM PUBLIC;

INSERT INTO twin.ai_policies ("Id","Version","RecordedAtUtc","ActorId","PromptVersion","AllowedToolsJson","ModelEnabled","DailyCallLimit","DailyBudgetUsd","CallReservationUsd")
VALUES (gen_random_uuid(),1,clock_timestamp(),'system','topic-selector/1.0',
  '["inspect_zone","forecast_queues","staffing_settings","read_evaluation","evaluate_staffing","prepare_recommendation","cancel_evaluation"]'::jsonb,true,100,1,0.01);
