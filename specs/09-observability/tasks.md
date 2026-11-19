# 09 Observability: Tasks

- [x] 1. Shared observability extension (do on Monday with spec 01 task 7)
  - `AddAuctionObservability`, `/metrics`, health endpoints, Seq sink, instance enrichment. Use in api, web, workers, simulator.
  - _Requirements: R1.1, R4.1, R4.2_

- [x] 2. Sensitive data masking
  - Destructuring policy + test.
  - _Requirements: R1.2_

- [x] 3. Metrics instruments
  - `AuctionMetrics` meters and every instrument in R2; wire calls in specs 03, 04, 05, 07 as those tasks land.
  - _Requirements: R2_

- [x] 4. Trace through stream
  - `corr` on stream entries, consumer activity links, log scope.
  - Check: trigger a bid error, search Seq by the UI ref → see Api and persister lines.
  - _Requirements: R1.3, R3.1, R3.2_

- [x] 5. Prometheus config and rules
  - `prometheus.yml`, `rules.yml` with all R6 alerts.
  - Check: stop workers → `NoLifecycleLeader` fires within ~30 s in Prometheus UI.
  - _Requirements: R6_

- [x] 6. Grafana board
  - Datasource + dashboard JSON provisioning, anonymous viewer.
  - _Requirements: R5.1_
