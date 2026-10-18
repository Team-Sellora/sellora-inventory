# Local setup

The database connection string is not committed to the repository. `ConnectionStrings:Default`
is empty in `appsettings.json`, and the service refuses to start until it is supplied.

## Local development (user-secrets)

Run once from the repository root:

```bash
dotnet user-secrets set "ConnectionStrings:Default" \
  "Host=localhost;Port=5435;Database=inventory_db;Username=sellora;Password=<your-local-password>" \
  --project src/Sellora.InventoryService.Api
```

User-secrets are loaded automatically when `ASPNETCORE_ENVIRONMENT=Development`
(the default for the launch profiles and for `dotnet ef`).

## Docker, CI and deployed environments

Set the environment variable:

```bash
ConnectionStrings__Default="Host=...;Port=...;Database=inventory_db;Username=...;Password=..."
```

## Tests

No setup needed. The integration tests start their own PostgreSQL container and inject its
connection string.
