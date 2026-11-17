# 09 Observability: Design

## 1. Shared setup (`Auction.Infrastructure/Observability/ObservabilityExtensions.cs`)

```csharp
public static WebApplicationBuilder AddAuctionObservability(this WebApplicationBuilder b, string service) {
  b.Host.UseSerilog((ctx, cfg) => cfg
      .ReadFrom.Configuration(ctx.Configuration)
      .Enrich.FromLogContext()
      .Enrich.WithProperty("service", service)
      .Enrich.WithProperty("instance", Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName)
      .Destructure.With<SensitiveDataDestructuringPolicy>()     // masks Email, drops Password/Token props
      .WriteTo.Console(new RenderedCompactJsonFormatter())
      .WriteTo.Seq(ctx.Configuration["Seq:Url"]!));
  b.Services.AddOpenTelemetry()
      .ConfigureResource(r => r.AddService(service))
      .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation()
                         .AddMeter("Auction.*").AddPrometheusExporter())
      .WithTracing(t => t.AddAspNetCoreInstrumentation().AddGrpcClientInstrumentation()
                         .AddSource("Auction.*"));    // export traces to Seq via OTLP (Seq ≥ 2024 supports OTLP)
  return b;
}
// app.MapPrometheusScrapingEndpoint("/metrics"); app.MapHealthChecks("/health/live", live); app.MapHealthChecks("/health/ready", ready);
```

Workers is a generic host; give it a minimal Kestrel (`WebApplication`) so it can serve `/metrics` and health on 7101.

Meters: one `Meter("Auction.Bidding")`, `Meter("Auction.Persist")`, `Meter("Auction.Lifecycle")`, `Meter("Auction.Live")`, `Meter("Auction.Notify")` defined in a static `AuctionMetrics` class.

## 2. Trace through the stream
- Api: bid handler runs inside the gRPC activity; passes `Activity.Current.TraceId` as `corr` ARGV to `bid.lua`, which stores it on the stream entry.
- Persister: for each entry starts `ActivitySource("Auction.Persist").StartActivity("persist", ActivityKind.Consumer, links: [new ActivityLink(ActivityContext from corr)])`.
- Log scope adds `correlation_id = corr`, so Seq search by the UI's ref shows Api + persister logs.

## 3. Prometheus
`deploy/prometheus/prometheus.yml`:
```yaml
global: { scrape_interval: 5s }
rule_files: [ "rules.yml" ]
scrape_configs:
  - job_name: api
    static_configs: [{ targets: ["api-1:7001", "api-2:7001"] }]
  - job_name: web
    static_configs: [{ targets: ["web-1:8080", "web-2:8080", "web-3:8080"] }]
  - job_name: workers
    static_configs: [{ targets: ["workers-1:7101", "workers-2:7101"] }]
  - job_name: simulator
    static_configs: [{ targets: ["simulator:7201"] }]
```
`rules.yml` example rows:
```yaml
- alert: PersistLag
  expr: max(persist_lag_ms) > 5000
  for: 1m
  labels: { severity: high }
- alert: BidLatency
  expr: histogram_quantile(0.99, sum by (le) (rate(bid_latency_ms_bucket[5m]))) > 350
  for: 5m
- alert: NoLifecycleLeader
  expr: sum(lifecycle_is_leader) == 0
  for: 30s
  labels: { severity: critical }
```

## 4. Grafana
`deploy/grafana/provisioning/datasources/prometheus.yml` and `dashboards/auction-mvp.json` (8 panels from R5). Anonymous viewer enabled for the demo (`GF_AUTH_ANONYMOUS_ENABLED=true`, role Viewer) on localhost only.

## 5. Sensitive data policy
`SensitiveDataDestructuringPolicy` + a Serilog `IMessageTemplateParser`-free approach: never log request objects that contain secrets. Auth handlers log `user_id` only. Unit test: log an object with `Password`, `AccessToken`, `Email` → output masks/removes them.
