# RSS Reader Web App (frontend)

A static web app that runs on WASM, written in C# with Blazor.

## Stack

C#, Blazor Webassembly, Javascript, HTML, CSS

## Hosting infrastructure

Azure Static Web App with easy auth enabled


# RSS Reader Web API Server (backend)

An ASP.NET Core Web API, targeting latest .NET, that powers the RSS Reader frontend by processing RSS feeds, storing data on disk in a SQLite database,
and making that data available through several HTTP endpoints.

## Stack

C#, ASP.NET, SQLite

## Hosting infrastructure

~~Azure app service standard b1s and public ip~~

~~Azure VM, Standard_B1s West US 2 region, public ip, and disk~~

Azure Container App instance with ephemeral storage, periodically backed up to azure files

## Endpoints (C# controllers)
* `/api/feed/*` - Retrieve, refresh, and import/export RSS feeds
* `/api/item/*` - Retrieve, search, and save RSS posts
* `/api/user/*` - Retrieve user info and login/register

## Database
SQLite database to store RSS feeds, items and users locally (only usernames/some metadata, not passwords. auth is handled externally by Azure App Service easy auth).
