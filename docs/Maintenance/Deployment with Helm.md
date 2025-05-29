# Deployment with Helm

AI Core services can be deployed to Kubernetes using Helm. The Helm charts are located in the [`./helm`](./helm) folder.

## Prerequisites

### Helm  
Ensure you have [Helm](https://helm.sh/docs/intro/install/) installed before proceeding.

### Traefik  
AI Core uses the [Traefik Ingress Controller](https://doc.traefik.io/traefik/) to route traffic. Make sure Traefik is deployed in your cluster before deploying AI Core:

```sh
helm repo add traefik https://helm.traefik.io/traefik 
helm repo update 
helm upgrade --install traefik traefik/traefik \
  --namespace kube-system \
  --version 29.0.1 
```

### Cert-Manager  
AI Core can use [Cert-Manager](https://cert-manager.io/) to automatically provision and renew TLS certificates via a cluster issuer. Ensure Cert-Manager is installed in your cluster before deploying AI Core:

```sh
helm repo add jetstack https://charts.jetstack.io --force-update
helm repo update
helm upgrade --install cert-manager jetstack/cert-manager \
  --namespace cert-manager \
  --create-namespace \
  --version v1.17.0 \
  --set crds.enabled=true
```

The default static configuration can be also installed as follows:

```sh
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.17.0/cert-manager.yaml
```

## Required Configuration

The Helm charts include a `values.yaml` file that defines default configurations. However, the following values must be explicitly specified during deployment:

- **`global.app.domain`**: The application domain (e.g., `ai.example.com`).

You can set these values using a custom `values.yaml` file or by passing them directly via the `--set` flag in the Helm command:

```sh
helm install ai-core ./helm \
  --set global.app.domain=ai.example.com
```

## Deployment Examples

### Minimal Deployment
Deploy AI Core API with an internal PostgreSQL instance, using the latest images from DockerHub. The File Ingestion service is not deployed. The TLS certificate will be automatically generated with cert-manager.

```sh
helm upgrade aicore "./helm" --namespace $namespace --create-namespace --install --kubeconfig $kubeconfig \
    --set global.app.domain=$hostName 
```

### Deploying with explicit certificate
Deploy AI Core API with an internal PostgreSQL instance, using the latest images from DockerHub. The File Ingestion service is not deployed.


```sh
helm upgrade aicore "./helm" --namespace $namespace --create-namespace --install --kubeconfig $kubeconfig \
    --set global.app.domain=$hostName \
    --set global.tls.createSecret=true \
    --set global.tls.crt=$tlsCrt \
    --set global.tls.key=$tlsKey
```

### Deploying from Azure Container Registry
Deploy AI Core API with an internal PostgreSQL instance, using images from Azure Container Registry with specified tags. The File Ingestion service is not deployed.

```sh
helm upgrade aicore "./helm" --namespace $namespace --create-namespace --install --kubeconfig $kubeconfig \
    --set global.app.domain=$hostName \
    --set global.containerRegistry.name=$acrName.azurecr.io \
    --set global.containerRegistry.dockerConfig=$containerRegistryAuthBase64 \
    --set global.aicore.service.tag=$aiCoreTag \
    --set global.ingestion.service.tag=$ingestionTag
```

### Deploying from Azure Container Registry with Entra ID Authentication
If using Azure Kubernetes Service with a managed identity that has access to ACR, you do not need to specify the `dockerConfig` file.

```sh
helm upgrade aicore "./helm" --namespace $namespace --create-namespace --install --kubeconfig $kubeconfig \
    --set global.app.domain=$hostName \
    --set global.containerRegistry.name=$acrName.azurecr.io \
    --set global.aicore.service.tag=$aiCoreTag \
    --set global.ingestion.service.tag=$ingestionTag
```

### Deploying with an External PostgreSQL Server
In this example, AI Core will use an external PostgreSQL server.

```sh
helm upgrade aicore "./helm" --namespace $namespace --create-namespace --install --kubeconfig $kubeconfig \
    --set global.app.domain=$hostName \
    --set global.containerRegistry.name=$acrName.azurecr.io \
    --set global.aicore.service.tag=$aiCoreTag \
    --set global.ingestion.service.tag=$ingestionTag \
    --set-string api.postgres.internal='False' \
    --set api.postgres.host=$dbHost \
    --set api.postgres.port=5432 \
    --set api.postgres.userName=$dbAdministratorLogin \
    --set api.postgres.password=$dbAdministratorPassword
```

## Values File Reference

### Global Values

| Key | Default Value | Description |
|------|---------------|-------------|
| `global.tls.createSecret` | `true` | If `true`, a Kubernetes secret will be created to store the provided TLS certificate. If `false`, the certificate will be automatically issued using the cert-manager.io cluster issuer. |
| `global.tls.crt` |  | Base64-encoded TLS certificate. Required if `tls.createSecret` is `true`. Ignored otherwise. |
| `global.tls.key` |  | Base64-encoded TLS private key. Required if `tls.createSecret` is `true`. Ignored otherwise. |
| `fileIngestion.enabled` | `false` | Enable or disable File Ingestion service |
| `global.app.name` | `aicore` | Application name |
| `global.app.domain` |  | Application domain |
| `global.app.logLevel` | `Information` | Logging level |
| `global.app.enableMonitoring` | `false` | Enable monitoring |
| `global.containerRegistry.dockerConfig` |  | Docker registry configuration |
| `global.containerRegistry.name` | `docker.io/viacode` | Container registry name |
| `global.containerRegistry.imagePullPolicy` | `Always` | Image pull policy |
| `global.environment.namespace` | `aicore-ns` | Kubernetes namespace |
| `global.environment.name` | `myapp` | Environment name |
| `global.aicore.service.tag` | `latest` | AI Core service image tag |
| `global.aicore.service.port` | `8005` | AI Core service port |
| `global.ingestion.maxParallelism` | `2` | Maximum parallel ingestion operations |
| `global.ingestion.requestTimeout` | `"00:15:00"` | Ingestion request timeout |
| `global.ingestion.service.tag` | `latest` | Ingestion service image tag |
| `global.ingestion.service.urlPrefix` | `"ingestion-api"` | Ingestion service URL prefix |
| `global.ingestion.service.port` | `8021` | Ingestion service port |
| `global.ingestion.qdrant.port` | `8016` | Qdrant service port |

### AI Core API Values

| Key | Default Value | Description |
|------|---------------|-------------|
| `api.postgres.storageSize` | `16Gi` | PostgreSQL storage size |
| `api.postgres.internal` | `True` | Use internal PostgreSQL |
| `api.postgres.host` |  | External PostgreSQL host |
| `api.postgres.port` | `5432` | PostgreSQL port |
| `api.postgres.dbName` | `aicoredb` | Database name |
| `api.postgres.userName` | `aicoredbuser@viacode.com` | Database user name |
| `api.postgres.password` | `default` | Database password |
| `api.redis.port` | `6379` | Redis cache port |
| `api.redis.userName` | `aicatalyst@viacode.com` | Redis cache user name |
| `api.redis.password` | `AiCatalystIsTheBest!` | Redis cache password |
| `api.aicore.containerPort` | `8080` | AI Core service container port |
| `api.aicore.ingestion.delay` | `10` | |
| `api.aicore.ingestion.maxFileSize` | `209715200` | |


### File Ingestion Service Values

| Key | Default Value | Description |
|------|---------------|-------------|
| `fileIngestion.service.containerPort` | `7880` | Service container port |
| `fileIngestion.qdrant.containerPort` | `6333` | Qdrant container port |
