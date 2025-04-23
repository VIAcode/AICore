# Deployment with Docker

This guide explains how to deploy AI Core using the pre-built Docker images. Follow these steps to get the solution up and running efficiently.

---

## Prerequisites

Before deploying, ensure you have the following installed and configured on your system:

1. **Docker**:
   - Download and install Docker from [Docker's official website](https://www.docker.com/).
   - Verify the installation by running:
     ```bash
     docker --version
     ```
2. **Docker Hub Account** (Optional):
   - If the Docker images are private, log in to Docker Hub using your credentials:
     ```bash
     docker login
     ```

---

## Steps to Deploy the Solution

### 1. Pull the Docker Images

The pre-built Docker images for AI Core are hosted on Docker Hub. To download the images, run:

#### Ingestion Service Image
```bash
docker pull viacode/ai-core-file-ingestion:latest
```

#### API Service Image
```bash
docker pull viacode/ai-core:latest
```

---

### 2. Create Environment Configuration (Optional)

Some configurations may require environment variables. Create a `.env` file in your project directory to define these variables:

```env
APP_ENV=production
APP_PORT=8080
DB_HOST=your-database-host
DB_USER=your-database-user
DB_PASSWORD=your-database-password
INGESTION_PORT=5000
API_PORT=8081
```

Ensure you replace the placeholders with actual values.

---

### 3. Run the Docker Containers

#### 3.1 Start the Ingestion Service Container

Start the ingestion service container using the following command:

```bash
docker run -d \
  --name ai-core-file-ingestion \
  -p 8080:8080 \
  --env-file .env \
  viacode/ai-core-file-ingestion:latest
```

#### 3.2 Start the API Service Container

Start the API service container using the following command:

```bash
docker run -d \
  --name ai-core \
  -p 8081:8081 \
  --env-file .env \
  viacode/ai-core:latest
```

---

### 4. Verify the Deployment

To ensure the containers are running, execute:

```bash
docker ps
```

You should see both containers listed. Verify the logs to ensure everything is working correctly:

#### Ingestion Service Logs
```bash
docker logs ai-core-file-ingestion
```

#### API Service Logs
```bash
docker logs ai-core
```

Access the services in your browser:

- Ingestion Service: `http://localhost:8080`
- API Service: `http://localhost:8081`

---

### 5. Manage the Docker Containers

Here are some useful commands to manage the containers:

- **Stop the containers**:
  ```bash
  docker stop ai-core-file-ingestion ai-core
  ```

- **Restart the containers**:
  ```bash
  docker restart ai-core-file-ingestion ai-core
  ```

- **Remove the containers**:
  ```bash
  docker rm ai-core-file-ingestion ai-core
  ```

- **Remove the Docker images** (if needed):
  ```bash
  docker rmi viacode/ai-core-file-ingestion:latest viacode/ai-core:latest
  ```

---
 
Happy deploying!