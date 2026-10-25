# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files and restore dependencies
COPY ["src/Server/Server.csproj", "Server/"]
COPY ["src/Shared/Shared.csproj", "Shared/"]
RUN dotnet restore "Server/Server.csproj"

# Copy everything else and build
COPY ["src/Server/", "Server/"]
COPY ["src/Shared/", "Shared/"]
WORKDIR "/src/Server"
RUN dotnet build "Server.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
# ReadyToRun precompiles the app and its dependencies to native code so the
# single 0.5 vCPU replica spends less of every cold start in the JIT. A RID is
# required for R2R; the image stays framework-dependent. The durability test
# in CI (test/durability) passes READY_TO_RUN=false: it measures handover
# behaviour, not cold-start time, and R2R is most of the image build.
ARG READY_TO_RUN=true
RUN dotnet publish "Server.csproj" -c Release -r linux-x64 --self-contained false     -o /app/publish /p:UseAppHost=false /p:PublishReadyToRun=${READY_TO_RUN}

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

# Links the published GHCR package to this repository, which is what lets the
# workflow's GITHUB_TOKEN push it -- no personal access token involved.
LABEL org.opencontainers.image.source=https://github.com/brandonchastain/rss-reader

WORKDIR /app

# Install sqlite3 CLI for cold-start quick_check on the seeded DB
RUN apt-get update && apt-get install -y --no-install-recommends sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# Install Litestream for continuous SQLite replication to Azure Blob Storage.
# 0.5.17: 0.5.9 took a full snapshot (the whole database, about 230 MB) on
# every WAL restart around a checkpoint, every minute or two under this app's
# write pattern (BLOB-DURABILITY-SPEC.md 11.7); 0.5.15 hardened that path.
ADD --checksum=sha256:cfb371176d164437ae869f8351cfde49bd1804ae71c61923f75c9cba9c9c006d \
    https://github.com/benbjohnson/litestream/releases/download/v0.5.17/litestream-0.5.17-linux-x86_64.tar.gz \
    /tmp/litestream.tar.gz
RUN tar -C /usr/local/bin -xzf /tmp/litestream.tar.gz && rm /tmp/litestream.tar.gz

# Create directory for SQLite database (will be mounted from Azure Files)
RUN mkdir -p /data

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=publish /app/publish .
COPY infrastructure/litestream.yml /etc/litestream.yml
COPY infrastructure/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

ENTRYPOINT ["/app/docker-entrypoint.sh"]
