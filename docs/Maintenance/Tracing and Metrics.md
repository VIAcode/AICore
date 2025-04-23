# OpenTelemetry Configuration

This application supports [OpenTelemetry](https://opentelemetry.io/) for tracing and metrics. Exporters are configurable at runtime using environment variables. The following environment variables control the behavior:

## General Settings

| Variable | Description | Example |
|----------|-------------|---------|
| `OTEL_SERVICE_NAME` | Logical name of the service. Used in traces and metrics. | `aicore` |
| `OTEL_RESOURCE_ATTRIBUTES` | Additional resource attributes. Comma-separated key-value pairs. | `env=prod,region=westeurope` |

## Exporters

| Variable | Description | Example |
|----------|-------------|---------|
| `OTEL_TRACES_EXPORTER` | Comma-separated list of trace exporters. Supported: `none`, `otlp`, `azuremonitor` | `otlp,azuremonitor` |
| `OTEL_METRICS_EXPORTER` | Comma-separated list of metrics exporters. Supported: `none`, `prometheus`, `azuremonitor` | `prometheus,azuremonitor` |

## OTLP Exporter (default port: 4317)

| Variable | Description | Example |
|----------|-------------|---------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP collector endpoint for traces and metrics. | `http://otel-collector:4317` |

## Azure Monitor Exporter (App Insights)

| Variable | Description | Example |
|----------|-------------|---------|
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Connection string for Azure Application Insights. | `InstrumentationKey=...;IngestionEndpoint=...` |

## Prometheus Exporter

Prometheus metrics are exposed at `/metrics` on the app's HTTP endpoint (default: port 80 or your container’s configured port). No additional configuration needed other than enabling the exporter.
