# Testing (non-containerized)
Just hit F5 in Visual Studio, it should launch the frontend and backend along with a browser window.

# Testing the local Web API containerized build (with docker)

This guide shows how to build and test your RSS Reader API locally using Docker before deploying to Azure Container Apps.

## Build the Docker Image

```powershell
# Navigate to the src directory (parent of Server and Shared)
cd c:\dev\rssreader\rss-reader\src

# Build the Docker image
docker build -f Server/Dockerfile -t rss-reader-api:local .
```

## Run Locally with Persistent Storage

```powershell
# Create a local directory for the SQLite database
mkdir c:\dev\rssreader\docker-data -Force

# Run the container with volume mount
docker run -d `
  --name rss-reader-test `
  -p 8080:8080 `
  -v c:\dev\rssreader\docker-data:/data `
  rss-reader-api:local

# View logs
docker logs -f rss-reader-test
```

## Test the API

```powershell
# Test your endpoints
curl http://localhost:8080/api/feed

# Or open in browser
start http://localhost:8080/api/feed
```

## Useful Docker Commands

### Container Management
```powershell
# View running containers
docker ps

# View all containers (including stopped)
docker ps -a

# Stop the container
docker stop rss-reader-test

# Start it again
docker start rss-reader-test

# Restart the container
docker restart rss-reader-test

# Remove the container
docker rm -f rss-reader-test

# View container logs
docker logs rss-reader-test

# Follow logs in real-time
docker logs -f rss-reader-test

# View last 50 lines of logs
docker logs --tail 50 rss-reader-test
```

### Debugging
```powershell
# Interactive shell inside container
docker exec -it rss-reader-test /bin/bash

# Check if database file was created
ls c:\dev\rssreader\docker-data

# View container resource usage
docker stats rss-reader-test

# Inspect container configuration
docker inspect rss-reader-test
```

### Image Management
```powershell
# List all images
docker images

# Remove old image
docker rmi rss-reader-api:local

# Remove all unused images
docker image prune
```

## Rebuild After Code Changes

```powershell
# Stop and remove old container
docker rm -f rss-reader-test

# Rebuild image
docker build -f Server/Dockerfile -t rss-reader-api:local .

# Run new container
docker run -d `
  --name rss-reader-test `
  -p 8080:8080 `
  -v c:\dev\rssreader\docker-data:/data `
  rss-reader-api:local

# Check logs
docker logs -f rss-reader-test
```

## Configuration Notes

### Database Paths
The `appsettings.json` uses absolute paths that work in Docker:
```json
"UserDb": "/tmp/storage.db",
"ItemDb": "/tmp/storage.db",
"FeedDb": "/tmp/storage.db"
```

The active db from `/tmp/` will be copied to the container's `/data/` folder.

That `/data/` folder maps to `c:\dev\rssreader\docker-data\storage.db` on your host machine via the volume mount.

### Volume Mount
The `-v c:\dev\rssreader\docker-data:/data` flag mounts your local directory to `/data` inside the container. This means:
- Database persists even when container is deleted
- You can inspect/backup the database file directly on your machine
- Multiple container rebuilds can reuse the same data

### Port Mapping
The `-p 8080:8080` flag maps port 8080 on your machine to port 8080 in the container. Access the API at `http://localhost:8080`.

## Troubleshooting

### Container won't start
```powershell
# Check logs for errors
docker logs rss-reader-test

# Verify image built successfully
docker images | Select-String "rss-reader-api"
```

### Database errors
```powershell
# Check if /tmp/ directory contains the active db in container
docker exec rss-reader-test ls -la /tmp/

# Check permissions on host directory
Get-Acl c:\dev\rssreader\docker-data
```

### Port already in use
```powershell
# Find what's using port 8080
netstat -ano | Select-String ":8080"

# Use a different port
docker run -d --name rss-reader-test -p 9090:8080 `
  -v c:\dev\rssreader\docker-data:/data `
  rss-reader-api:local

# Access at http://localhost:9090 instead
```

### Container stops immediately
```powershell
# Run in foreground to see errors
docker run --rm `
  --name rss-reader-test `
  -p 8080:8080 `
  -v c:\dev\rssreader\docker-data:/data `
  rss-reader-api:local
```

## Clean Up

### Remove everything
```powershell
# Stop and remove container
docker rm -f rss-reader-test

# Remove image
docker rmi rss-reader-api:local

# Remove database (optional - deletes all data!)
Remove-Item -Recurse -Force c:\dev\rssreader\docker-data
```

### Keep database, clean up containers/images
```powershell
# Remove container
docker rm -f rss-reader-test

# Remove image
docker rmi rss-reader-api:local

# Database at c:\dev\rssreader\docker-data is preserved
```

## Next Steps

Once local testing is complete, follow [deploy-container-app.md](deploy-container-app.md) to deploy to Azure Container Apps.
