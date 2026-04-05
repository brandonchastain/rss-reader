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
RUN dotnet publish "Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Install Litestream for continuous SQLite replication to Azure Blob Storage
ADD --checksum=sha256:e8612ef5424802723e8cfa2d07a182df60f9af71839b5ff5ef1e80dff38efbdd \
    https://github.com/benbjohnson/litestream/releases/download/v0.5.9/litestream-0.5.9-linux-x86_64.tar.gz \
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
