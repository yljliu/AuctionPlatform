# 09 Observability: Requirements

Covers: logs, metrics, traces, health, the Grafana board, and the alert rules (rules evaluated in Prometheus; routing via Alertmanager is deferred). Design refs: v2 sections 7.1, 7.3.

## R1 Logs
1. EVERY service SHALL write structured logs (Serilog) to the console and to Seq, with `service`, `instance`, `correlation_id`, `user_id` (when known) and `auction_id` (when known).
2. THE SYSTEM SHALL NOT log passwords, tokens, refresh tokens or full emails (mask as `j***@domain`).
3. WHEN an `INTERNAL` error is returned THE SYSTEM SHALL log the exception with the correlation id at Error level.

## R2 Metrics
THE SYSTEM SHALL expose Prometheus metrics on `/metrics` for api, web, workers and simulator, including at least:

| Metric | Type | From |
|---|---|---|
| `grpc_server_*` (built-in) | histogram/counter | api |
| `bids_total{result}` | counter | api |
| `bid_rejections_total{code}` | counter | api |
| `bid_latency_ms` | histogram | api |
| `persist_lag_ms` | gauge | workers |
| `persist_batch_size` | histogram | workers |
| `persist_dlq_total` | counter | workers |
| `settlement_lag_seconds` | gauge (max over sealed auctions) | workers |
| `lifecycle_is_leader` | gauge 0/1 | workers |
| `live_read_delay_ms` | histogram | api |
| `live_fanout_delay_ms` | histogram | web |
| `live_streams_open` | gauge | web |
| `circuits_active` | gauge | web |
| `notifications_created_total{type}` | counter | workers |
| `redis_replica_connected` | gauge 0/1 | api |

## R3 Traces
1. THE SYSTEM SHALL create one trace per request across web → api, and carry the trace id through the stream entry (`corr` field) so the persister's span links to the bid.
2. The correlation id shown in the UI SHALL equal the trace id.

## R4 Health
1. `/health/live` SHALL return 200 while the process runs.
2. `/health/ready` SHALL return 503 when a required dependency (SQL, Redis; for api also the replica when `RequireReplicaAck` is on) is down.

## R5 Dashboard
1. Grafana SHALL be provisioned at start with one board "Auction MVP": bids/s, bid latency P50/P99, rejections by code, persist lag, live fan-out delay P99, settlement lag, active circuits, Redis memory.

## R6 Alert rules (Prometheus rules file)
THE SYSTEM SHALL define these rules, visible in the Prometheus Alerts page:
`ApiDown`, `BidErrorRate`, `BidLatency`, `PersistLag` (> 5 s for 1 min), `PersistDLQ` (any increase), `SettlementLag` (> 30 s), `RedisReplicaDown`, `NoLifecycleLeader` (sum of `lifecycle_is_leader` == 0 for 30 s). Thresholds per v2 table 7.1.
