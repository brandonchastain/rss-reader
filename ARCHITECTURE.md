# Diagram

```
+-----------------------------------------------------+
|                RSS Reader                           |
|  +----------------------------------------------+   |
|  |  Frontend Web App - /src/WasmApp/            |   |
|  +----------------------------------------------+   |
|                 |   HTTP                            |
|                 |                                   |
|                 | /api/feed/*                       |
|                 | /api/item/*                       |
|                 | /api/user/*                       |
|                 v                                   |
|  +------------------------------------------+       |
|  | Azure Function API Proxy - /api/         |       |
|  +------------------------------------------+       |
|                 |   HTTP - forwarded                |
|                 |                                   |
|                 | /api/feed/*                       |
|                 | /api/item/*                       |
|                 | /api/user/*                       |
|                 v                                   |
|  +--------------------------------------------+     |
|  |  RSS Reader Backend Web API - /src/Server/ |     |
|  |                                            |     |
|  |        |                                   |     |
|  |        v                                   |     |
|  |    +-----------+                           |     |
|  |    |  SQLite   |                           |     |
|  |    | Database  |                           |     |
|  |    +-----------+                           |     |
|  +--------------------------------------------+     |
+-----------------------------------------------------+
```




## Frontend Web App

A static web app that runs on WASM, written in C# with Blazor.

#### Stack

C#, Blazor Webassembly, Javascript, HTML, CSS


## Backend Web API

An ASP.NET Core Web API that enables the frontend to update server state for users, feeds, and posts.

#### Stack

C#, ASP.NET, SQLite

#### Endpoints (C# controllers)
* `/api/feed/*` - Retrieve, refresh, and import/export RSS feeds
* `/api/item/*` - Retrieve, search, and save RSS posts
* `/api/user/*` - Retrieve user info and login/register

#### Database
SQLite database to store RSS feeds, posts, and user profiles.

