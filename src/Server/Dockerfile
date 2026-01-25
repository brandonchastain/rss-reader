# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj files and restore dependencies
COPY ["Server/Server.csproj", "Server/"]
COPY ["Shared/Shared.csproj", "Shared/"]
RUN dotnet restore "Server/Server.csproj"

# Copy everything else and build
COPY ["Server/", "Server/"]
COPY ["Shared/", "Shared/"]
WORKDIR "/src/Server"
RUN dotnet build "Server.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Create directory for SQLite database (will be mounted from Azure Files)
RUN mkdir -p /data

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Server.dll"]
