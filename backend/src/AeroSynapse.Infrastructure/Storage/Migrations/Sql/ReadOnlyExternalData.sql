CREATE TRIGGER decision_audit AFTER INSERT OR DELETE ON twin.external_data_snapshots
FOR EACH ROW EXECUTE FUNCTION twin.audit_capture();

CREATE FUNCTION twin.external_snapshot_immutable() RETURNS trigger LANGUAGE plpgsql
SET search_path = pg_catalog, twin, pg_temp AS $$
BEGIN
  IF TG_OP = 'DELETE' AND current_setting('aerosynapse.external_cleanup',true) = 'retention'
    AND OLD."RetrievedAtUtc" < clock_timestamp() - interval '7 days' THEN RETURN OLD; END IF;
  RAISE EXCEPTION 'Validated source snapshots are immutable.' USING ERRCODE = '42501';
END $$;
CREATE TRIGGER external_snapshot_immutable BEFORE UPDATE OR DELETE ON twin.external_data_snapshots
FOR EACH ROW EXECUTE FUNCTION twin.external_snapshot_immutable();
CREATE TRIGGER external_snapshot_no_truncate BEFORE TRUNCATE ON twin.external_data_snapshots
EXECUTE FUNCTION twin.audit_no_truncate();

CREATE FUNCTION twin.external_data_expire(retention_days integer) RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, twin, pg_temp AS $$
DECLARE removed bigint;
BEGIN
  IF retention_days NOT BETWEEN 7 AND 3650 THEN RAISE EXCEPTION 'Retention must be 7-3650 days.'; END IF;
  PERFORM set_config('aerosynapse.external_cleanup','retention',true);
  DELETE FROM twin.external_data_snapshots d WHERE d."RetrievedAtUtc" < clock_timestamp() - make_interval(days => retention_days)
    AND NOT EXISTS (SELECT 1 FROM twin.scenarios s WHERE s."DataSourceJson"->>'snapshotId' = d."Id"::text)
    AND NOT EXISTS (SELECT 1 FROM twin.runs r WHERE r."InputSnapshotJson"#>>'{scenario,scenario,dataSource,snapshotId}' = d."Id"::text);
  GET DIAGNOSTICS removed = ROW_COUNT;
  PERFORM set_config('aerosynapse.external_cleanup','',true);
  RETURN removed;
END $$;
REVOKE ALL ON FUNCTION twin.external_data_expire(integer) FROM PUBLIC;
