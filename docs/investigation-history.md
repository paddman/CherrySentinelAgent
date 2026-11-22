# Investigation history

Sentinel Brain saves each completed bounded investigation as an independent snapshot. A second run for the same incident creates a new `investigationId` and preserves the earlier decision, tool trace and evidence. The existing latest-analysis API continues to return the latest analysis for that incident.

Use the extended application, `app.main_v2:app`, which is also the Docker entry point. Both history endpoints require `X-Cherry-Tenant` and `X-Cherry-Api-Key`; the server derives the storage tenant from those authenticated credentials.

## API

| Method and path | Result |
|---|---|
| `POST /v1/investigations/run` | Runs the existing bounded investigator and saves the completed result before returning success. Existing request and response fields remain supported. |
| `GET /v1/investigations` | Returns summary rows in `items` and a nullable `nextCursor`. |
| `GET /v1/investigations/{investigation_id}` | Returns the full saved result, including its original embedded decision. |

History reads do not call the LLM, query Central, execute tools or replace the current analysis. Responses use camelCase JSON field names. List query parameters use the names below.

### List and paginate

| Query parameter | Meaning |
|---|---|
| `limit` | Page size, from 1 to 100; default 50. |
| `incident_id` | Optional exact incident ID, from 1 to 200 characters. Omit it to list all incidents in the authenticated tenant. |
| `before` | Optional investigation ID returned as the previous page's `nextCursor`. Keep the same incident filter when continuing. |

```bash
curl -G http://127.0.0.1:8088/v1/investigations \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me' \
  --data-urlencode 'incident_id=inc-1001' \
  --data-urlencode 'limit=20'
```

Summary rows include `investigationId`, `incidentId`, `analysisId`, `createdAtUtc`, classification, risk score, confidence, model, fallback state, stop reason, tool-call count and evidence count. Full traces and evidence are returned by the detail endpoint.

Results are ordered by creation time descending, then investigation ID descending for equal timestamps. If `nextCursor` is non-null, request the next page:

```bash
curl -G http://127.0.0.1:8088/v1/investigations \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me' \
  --data-urlencode 'incident_id=inc-1001' \
  --data-urlencode 'limit=20' \
  --data-urlencode 'before=NEXT_CURSOR_FROM_PREVIOUS_RESPONSE'
```

Pagination uses the last returned investigation as its boundary rather than a numeric offset. Newer runs inserted between page requests do not shift that boundary. A null cursor means there are no more rows at the time of the query. Start again without `before` to see newly completed runs.

Unknown IDs and IDs belonging to another tenant return the same `404` response. The same rule applies to pagination cursors; a cursor must also match the requested incident filter. Invalid query lengths or limits receive a validation error. History responses carry `Cache-Control: no-store`.

### Open a saved run

```bash
curl http://127.0.0.1:8088/v1/investigations/INVESTIGATION_ID \
  -H 'X-Cherry-Tenant: demo' \
  -H 'X-Cherry-Api-Key: change-me'
```

The returned plan, trace, decision and counts describe that run. They are not reconstructed from the incident's current analysis, which may have changed since the investigation completed.

## Interpret the evidence

| Response field | Interpretation |
|---|---|
| `evidenceCatalog` | The bounded, normalized evidence actually used to produce this run's decision. Resolve a citation's `refId` against this catalog. |
| `timeline` | Chronologically ordered entries containing `timestampUtc` and `refId`. Follow the reference to the catalog for source, kind, summary and attributes. |
| `untimedEvidenceRefs` | Catalog references without a usable timestamp. These records remain available as evidence but receive no invented event time. |
| `trace[].evidenceRefs` | Tool evidence retained in the decision's catalog. |
| `trace[].omittedEvidenceRefs` | Tool evidence excluded by the evidence limits; it was not part of the decision's evidence input. |

The timeline orders evidence by its supplied timestamp. It does not establish causation or independently confirm an attack sequence. The tool trace records when investigation steps ran; those execution times are separate from event timestamps.

ISO-8601 timestamps with offsets are normalized to UTC. Zeek numeric timestamps are interpreted as Unix seconds, and legacy timestamps without an offset are interpreted as UTC. Malformed or absent timestamps are treated as untimed. Tool evidence IDs are namespaced per investigation to prevent collisions with caller-supplied references; treat IDs as opaque and resolve them within the saved snapshot.

The catalog uses the configured evidence item and character limits. It is a decision snapshot rather than a complete archive of all incoming telemetry. Existing response approval requirements are preserved in each saved decision; opening history does not execute a recommended action.

## Upgrade, backup and rollback

The feature adds SQLite storage and indexes when `AuditStore` initializes. Restart or redeploy the service to initialize them. Existing analysis, audit and feedback data remain in place. Each snapshot and its investigation-success audit entry are committed together, and duplicate investigation IDs cannot overwrite an earlier snapshot.

Before upgrading, create a consistent backup of the configured `CHERRY_DATABASE_PATH`. For example, use SQLite's backup command with the actual database and backup paths:

```bash
sqlite3 /path/to/sentinel-brain.db ".backup '/path/to/sentinel-brain-before-history.db'"
```

SQLite's backup API includes committed data while the database uses WAL. Copying only the live `.db` file can miss committed changes still in the WAL. Keep the backup outside the application's active data directory and apply the same access restrictions as the original database.

To roll back the application, deploy the previous code and retain the added table and indexes. They are additive and do not require deleting investigation history or restoring an older database. That older application will not expose the history endpoints or save new snapshots. A later upgrade can read the retained snapshots.

## Current boundaries

- Only completed runs are saved. Failed and timed-out investigations remain audit-only; a timeout still returns `504`. If snapshot persistence fails, the request does not report a durable success. An analysis may already have completed before that failure.
- Runs completed before this feature cannot be reconstructed from the old count-only audit entries. History begins with new completed runs.
- Snapshots contain the validated result and normalized evidence, without adding a raw request or tenant connection configuration. Evidence may still contain sensitive information. The existing `masked` mode is not comprehensive secret redaction or encryption at rest.
- SQLite tenant isolation is enforced by authenticated API access and tenant-scoped queries. This does not provide PostgreSQL row-level security, per-tenant databases or tenant isolation inside a shared Cherry Central installation.
- History is append-only through this API. Automatic retention, deletion, quotas and storage-capacity management are not included; plan database capacity for retained snapshots.
- This feature provides an API for investigation history. It does not add a campaign connector, a Web Command Center or a WPF history screen.
