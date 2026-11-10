# Payments-BE
BE para el microservicio de cobros/pagos en C#/.NET

## Run with Docker

```powershell
docker compose --profile api up -d --build   # API + SQL Server + Redis + RabbitMQ
```

- API: `http://localhost:5150` (Swagger at `/swagger`, health at `/health`). Migrations run on startup.
- JWT: set `JWT_AUTHORITY` (env var or `.env`) to the issuer used by the API Gateway; without it every protected endpoint answers `401`.
- Pending bills arrive only through `DinningSessionReadyEvent` on RabbitMQ (management UI at `http://localhost:15672`, `guest`/`guest`).
- Infra only (for `dotnet run`): `docker compose up -d`.
