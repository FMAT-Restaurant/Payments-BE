# Payments-BE
BE para el microservicio de cobros/pagos en C#/.NET

## Local secrets

Passwords are never committed. Create a `.env` in the repo root (git-ignored):

```dotenv
MSSQL_SA_PASSWORD=<your local SQL Server sa password>
JWT_SECRET=<same HS256 secret as Auth-Backend and the API Gateway>
```

`dotnet run` and `dotnet test` read SQL Server from one user environment variable (tests always use their own `payments_tests*` databases):

```powershell
[Environment]::SetEnvironmentVariable("ConnectionStrings__SqlServer", "Server=localhost,14330;Database=payments;User Id=sa;Password=<your password>;TrustServerCertificate=True", "User")
```

## Run with Docker

```powershell
docker compose --profile api up -d --build   # API + SQL Server + Redis + RabbitMQ
```

- API: `http://localhost:5150` (Swagger at `/swagger`, health at `/health`). Migrations run on startup.
- Requires the `restaurant-network` network created by [API-Gateway](https://github.com/FMAT-Restaurant/API-Gateway) (start the gateway first; do not create the network by hand or the gateway compose refuses it). The gateway reaches this API as `http://payments-backend:8084`.
- JWT: set `JWT_SECRET` (env var or `.env`) to the same HS256 secret used by Auth-Backend and the gateway; without it every protected endpoint answers `401`.
- Pending bills arrive only through `DinningSessionReadyEvent` on RabbitMQ (management UI at `http://localhost:15672`, `guest`/`guest`).
- Infra only (for `dotnet run`): `docker compose up -d`.
