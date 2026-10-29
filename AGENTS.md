# Payments Service - Agent Instructions

## Project

`Payments-BE` is a .NET 10 microservice responsible for payment and billing processing (cobros y pagos) in the restaurant system.

It serves as the backend service for the Frontend application repository, communicating via an API Gateway.

## Architecture: MVC sin Vista (Model-Controller Backend)

The project follows an **MVC pattern without View**, where client requests from the Frontend are routed through an **API Gateway**:

```text
+-----------------------+
|  Frontend Repository  |  <--- The "View" (External Client)
+-----------------------+
           |
           | HTTP / JSON REST
           v
+-----------------------+
|      API Gateway      |  <--- Reverse Proxy / Routing
+-----------------------+
           |
           | HTTP / JSON REST
           v
+-------------------------------------------------------+
|                      Payments-BE                      |
|                                                       |
|  +-------------------------------------------------+  |
|  |           Controllers / Endpoints ("C")         |  |
|  |   - HTTP Routing & Parameter Binding            |  |
|  |   - Request Validation & Status Codes           |  |
|  |   - OpenAPI / Swagger Definitions               |  |
|  +-------------------------------------------------+  |
|                          |                            |
|                          v                            |
|  +-------------------------------------------------+  |
|  |                   Services                      |  |
|  |   - Payment Processing & Orchestration          |  |
|  |   - Transaction State Machine                   |  |
|  |   - Gateway Integration & Payment Logic         |  |
|  +-------------------------------------------------+  |
|                          |                            |
|                          v                            |
|  +-------------------------------------------------+  |
|  |            Models & Persistence ("M")           |  |
|  |   - Entities & Transaction Data Models          |  |
|  |   - DTOs (Request / Response Contracts)         |  |
|  |   - Database / Persistence Access               |  |
|  +-------------------------------------------------+  |
+-------------------------------------------------------+
```

### 1. Controllers / Endpoints (The "C")

The Controller/Endpoint layer is responsible for HTTP communication routed via the API Gateway:

- Exposing RESTful HTTP endpoints.
- Parsing and validating HTTP request bodies and query parameters.
- Returning appropriate HTTP status codes (`200 OK`, `201 Created`, `400 Bad Request`, `404 Not Found`, `500 Internal Server Error`).
- Exposing OpenAPI / Swagger metadata.

**Rules:**
- Controllers must remain thin.
- Do not place domain rules, transaction calculations, or direct external gateway logic inside Controllers.
- Delegate all business operations to the Services layer.

### 2. Services Layer

The Services layer contains the core business logic of the microservice:

- Orchestrating payment operations (authorization, capture, refunds, cancellations).
- Managing transaction statuses and payment lifecycle rules.
- Integrating with external payment gateways or third-party providers.
- Emitting events or notifications when transactions complete.

**Rules:**
- Keep Services decoupled from HTTP transport specifics (`HttpContext`, `HttpRequest`).
- Ensure operations are idempotent where applicable.

### 3. Models and DTOs (The "M")

The Model layer represents data structures and domain representations:

- **DTOs (Data Transfer Objects):** Define clean, stable contracts for input and output with the Frontend (routed via the API Gateway).
- **Entities / Domain Models:** Represent the state of payments, invoices, accounts, and transactions.
- **Data Access:** Repositories or DbContext for persistence operations.

**Rules:**
- Do not expose internal persistence entities directly to the API if a DTO is required to maintain a decoupled contract.
- Keep Models focused on data representation and invariants.

---

## Messaging Decisions (PMC-5)

Agreed in PR #2 review. Do not change without team agreement:

- **Broker:** RabbitMQ (same broker Órdenes publishes to). Connection from `ConnectionStrings:RabbitMq`; the app fails at startup if missing, on purpose.
- **Message contract:** `DinningSessionReadyEvent` uses a fixed `[MessageUrn("DinningSessionReadyEvent")]`; Órdenes must publish with the same URN. Keep the `Dinning` spelling as-is. Shared contracts package vs. fixed URN is pending a meeting with the team representatives; keep the fixed URN meanwhile.
- **Storage:** pending bills live in Redis (`IDistributedCache`, keys `payments:bill:{dinningSessionId}` and `payments:event:{eventId}`) with a 24 h TTL. No SQL table for now.
- **Idempotency:** simple check-then-set by `eventId` in Redis. No `SET NX` or MassTransit Outbox/Inbox; concurrent duplicates write the same data, which is harmless.
- **Ordering:** an event whose `Timestamp` is older than the saved snapshot is discarded.
- **Validation:** done inside the consumer. Move to a generic `UseConsumeFilter(typeof(ValidationFilter<>))` only when there is more than one consumer.
- **DLQ:** failed messages go to the `<endpoint>_error` queue. Monitoring/reprocessing owner is still undefined (to be agreed with whoever owns the event queue).
- **MassTransit license:** pinned to 8.5.x (Apache 2.0); do not upgrade to v9 (commercial). Planned migration target: `RabbitMQ.Client` directly.

---

## Payment Decisions (PMC-6)

Agreed while implementing PMC-6. Several items are pending confirmation with the story owners (see the PMC-6 PR):

- **Database:** SQL Server via EF Core (`ConnectionStrings:SqlServer`), as referenced by PMC-13/PMC-16. Money is `decimal(18,2)`; enums are stored as text; keys are EF sequential GUIDs. Migrations live in `PaymentsModule/Data/Migrations`.
- **Local infrastructure:** `docker compose up -d` (project name `payments`). SQL Server is published on port **14330** to avoid a native SQL Server install and other local stacks (e.g. Booking-service).
- **Source of truth for the amount due:** the pending bill saved by PMC-5 in Redis. `detalles_snapshot` in the request is validated but not trusted; the persisted items come from the bill. Amount due = `ExpectedTotal + propina`; `Total` = sum of `PaymentBreakdown`.
- **Change:** payments are capped to the amount owed (electronic first, cash absorbs the excess), so change is never stored as income. Electronic payments exceeding the total are rejected (422).
- **Idempotency:** unique index on `DinningSessionId` plus a prior check; both paths return 409.
- **Event:** `PaymentCompletedEvent` with fixed `[MessageUrn("PaymentCompletedEvent")]`, published through the MassTransit EF **bus outbox** (only delivered after COMMIT). This outbox applies to publishes from services; the PMC-5 consumer keeps its Redis idempotency.
- **Folio:** `TX-{yyyyMMdd}-{number:D6}` from the global sequence `transaction_number_seq` (UTC date, does not restart daily).
- **Identity:** JWT Bearer configured in `Authentication:Schemes:Bearer`; the cashier is the `NameIdentifier` (`sub`) claim, never the payload.
- **Cash shift:** `CashShift` has the shape required by PMC-8 (open/close endpoints belong to PMC-8). Charging requires an `Open` shift of the cashier (422 otherwise).
- **Tests:** `ChargeServiceTests` run against a real SQL Server (`payments_tests` database; override with `ConnectionStrings__SqlServer`). CI uses a SQL Server service container.

---

## Cash Shift Decisions (PMC-8)

Agreed while implementing and reviewing PMC-8:

- **Routes:** `POST /api/v1/cash-shifts/open` and `POST /api/v1/cash-shifts/close` follow the English `Code Language` convention (to be aligned with Frontend / API Gateway). Wire JSON property names keep the Spanish snake_case contract (`fondo_inicial`, `efectivo_declarado`, `turno_caja_id`, `estado`, `diferencia`, `mensaje`).
- **Required decimal fields:** `InitialFund` and `DeclaredCash` are `decimal?` on the request DTOs and validated with `.NotNull().GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true)` so an empty payload `{}` is rejected with `400 Bad Request` instead of defaulting to `0.00`.
- **Open shift concurrency:** a filtered unique index `IX_CashShifts_CashierId` (`WHERE [Status] = 'Open'`) plus a prior check guarantees at most one open shift per cashier; concurrent duplicates return `409 Conflict` (`RowVersion` only protects existing rows on `UPDATE`, whereas the filtered unique index prevents concurrent duplicate `INSERT`s).
- **Close vs. charge concurrency:** both `ChargeService.ChargeAsync` and `CashShiftService.CloseAsync` acquire a row lock on the open `CashShift` (`WITH (UPDLOCK, ROWLOCK, INDEX(IX_CashShifts_CashierId))` and literal `Status = 'Open'`) inside their `ReadCommitted` transaction before reading or inserting `Transactions`. The index hint and `'Open'` literal ensure an index seek that locks only that cashier's open shift without blocking other cashiers. `RowVersion` remains as the optimistic concurrency token on `CashShift`.
- **Tips in shift reconciliation:** tips are accumulated in `TipsTotal` and excluded from `ExpectedCash` and `CardTotal` so they are never double-counted and cash tips taken by the waiter do not unbalance the drawer (`netCardSale = min(cardPaid, total - tip)`, `netCashSale = (total - tip) - netCardSale`, `ExpectedCash = InitialFund + sum(netCashSale)`). Pending confirmation with the story owner, including whether card tips are paid out to waiters in cash from the drawer (which would subtract card tips from `ExpectedCash`) or settled separately.
- **Identity & validation conventions:** cashier identity is read via `User.FindFirstValue(ClaimTypes.NameIdentifier)` in the controller rather than injecting `IHttpContextAccessor` into services (keeping Services decoupled from `HttpContext`), and validator classes use English names (`OpenCashShiftRequestValidator`, `CloseCashShiftRequestValidator`).
- **Reconciliation difference & messages:** `Difference = DeclaredCash - ExpectedCash` (negative on shortage, positive on surplus, `0.00` on exact match). Exact match transitions status to `Closed` (`CERRADO`, `"Corte exacto. Turno cerrado."`); non-zero transitions to `Unbalanced` (`DESCUADRADO`, `"Corte con faltante. Turno cerrado con descuadre."` or `"Corte con sobrante. Turno cerrado con descuadre."`).
- **Closing without open shift:** returns `422 Unprocessable Entity` (`ProblemDetails`), matching `ChargeService` when no open shift exists.
- **Persisted summary:** `CashShift` stores `DeclaredCash`, `ExpectedCash`, `CardTotal`, `TipsTotal`, and `Difference` in the database while the close response returns the 4 contract fields.

---

## Sale Cancellation Decisions (PMC-12)

Confirmed by the PO (PMC-12-P1 to P13) unless marked **PENDING**:

- **Route and roles:** `POST /api/v1/pagos/{folio}/cancelar`, protected with `[Authorize(Roles = "Gerente,Administrador,Admin")]` (exact, case-sensitive values). The PO decided that both `Administrador` (this story) and `Admin` (PMC-16) are accepted. A `Cajero` gets `403`, a missing token `401`. The role claim may arrive as `role` or `ClaimTypes.Role`: `AddJwtBearer()` keeps its default claim mapping (`MapInboundClaims = true`), which turns `role` into `ClaimTypes.Role` and `sub` into `NameIdentifier`, so `Program.cs` needs no extra code. `Jwt_bearer_maps_the_role_and_sub_claims_so_the_endpoint_authorizes` checks it with a real signed JWT; if anyone sets `MapInboundClaims = false`, that test fails.
- **Identity:** the authorizer is the `NameIdentifier` claim read in the controller and passed to the service. The story suggests `IHttpContextAccessor`; the PMC-8 convention (Services decoupled from `HttpContext`) wins.
- **Naming:** code uses the repo's English names (`PaymentTransaction`, `PaymentBreakdown`, `SaleCancellation`) for the story's `TransaccionPago`, `DesglosePago`, `CancelacionVenta`. The table is `SaleCancellations`. The wire contract keeps `motivo`, `folio`, `estado`, `fecha_cancelacion`, `mensaje`.
- **HTTP codes:** `200` cancelled, `400` invalid reason or body, `401` no identity, `403` wrong role, `404` unknown folio (exact, case-sensitive, opaque string; SQL Server's case-insensitive collation is compensated with an ordinal comparison), `409` already cancelled (`"The transaction has already been cancelled"`, exact text), `422` a card payment without authorization reference (checked before any refund), `502` the gateway could not refund (503 after retries, 4xx or timeout).
- **Language of errors:** the PO decided that error texts are in English, so the `409` is `"The transaction has already been cancelled"` (the story's Gherkin has it in Spanish) and the `400` for an empty reason is fixed in English instead of depending on the server culture. The success message below keeps the contract text of the story.
- **Success message:** always `"Extorno procesado exitosamente. Se generó el ticket de cancelación."`, cash-only or card. No ticket entity is created; it is only response text.
- **Reason:** required, trimmed, 1 to 500 characters, stored as `nvarchar(500)`.
- **State and audit:** the sale is never deleted. `Status` goes `COMPLETADO` to `CANCELADO` and one `SaleCancellation` row is inserted in the same `ReadCommitted` transaction (`AuthorizerId`, `Reason`, `CancelledAt`, `GatewayReference`, `RefundedAmount`). A unique index on `TransactionId` allows one cancellation per sale.
- **Concurrency:** the sale row is read with `WITH (UPDLOCK, ROWLOCK)` before anything else, so a double click waits, then finds the sale cancelled (`409`) and the card is never refunded twice. The lock is held while the gateway is called: every card refund has its own 30 s budget (`BankGatewayOptions.Timeout`, retries included), so a sale with N card payments can hold the lock for up to N x 30 s plus the backoff. There is no total budget for the whole cancellation on purpose (see Refunds). The unique index is the last line of defence.
- **Refunds:** only card payments (`TARJETA_CREDITO` and `TARJETA_DEBITO`) go through the gateway, one call per card `PaymentBreakdown`, for its stored `Amount` (a tip charged to the card is included). Cash never goes through the gateway. If any call fails, nothing is committed and the sale stays `COMPLETADO`. From the first refund to the COMMIT the service ignores the request's `CancellationToken` (`CancellationToken.None`): if the client disconnects after card 1 was refunded, aborting would roll the sale back with money already returned and no audit row. Each call stays bounded by the Polly timeout, and `Client_disconnecting_after_the_first_refund_still_completes_the_cancellation` covers it.
- **Gateway client:** `IBankGatewayClient` is a typed `HttpClient` with a Polly pipeline from `Microsoft.Extensions.Http.Resilience`: 3 retries with exponential backoff (5xx, 408, 429 and network errors; other 4xx are final) inside a 30 s timeout. There is no real gateway or sandbox, so `BankGateway:UseMock` is `false` by default (an environment without a configured gateway answers `502` instead of pretending to refund) and only `appsettings.Development.json` turns it on; `MockBankGatewayHandler` answers the status in `BankGateway:MockStatusCode` (200 or 503). A broken circuit (`BrokenCircuitException`, expected once PMC-7 adds its circuit breaker to this pipeline) is mapped to `BankGatewayException` too, so it answers `502` and not `500`; `PMC-7` scenario 3 (automatic refund when the database fails) can reuse `RefundAsync`.
- **Recharge:** a cancelled session cannot be charged again; the unique index on `DinningSessionId` is kept and PMC-6 is not touched.
- **Closed shifts:** cancelling is allowed without checking the shift (no extra cash validation). A cancelled sale no longer counts in a later close because `CloseAsync` only sums `Completed` rows. The totals already saved on an already closed shift are not recalculated.
- **Events:** no outbox event is emitted for cancellations for now.
- **Tests:** `SaleCancellationTests` and `BankGatewayClientTests` run against the real SQL Server (`payments_tests`, migrated with `ChargeServiceTests.EnsureMigratedAsync`) with a scripted HTTP handler as the bank. RBAC is checked through a real `TestServer` pipeline, because calling the controller directly skips the authorization filter. The scripted handler reads the request body as text, because the retry pipeline resends the same `HttpRequestMessage` and a stream read would close the cached `JsonContent` stream.
- **Packages:** `Microsoft.Extensions.Http.Resilience` `10.0.0` and `Microsoft.AspNetCore.TestHost` `10.0.12`; restore, build and NuGet audit pass with no warnings.

**PENDING** (not confirmed, to be validated before relying on them):

- **Real gateway contract:** the endpoint (`POST refunds`), the JSON fields (`folio`, `referencia_autorizacion`, `monto`, `referencia_extorno`), the `Idempotency-Key` header and the gateway's support for it are provisional.
- **Gateway succeeded but COMMIT failed:** the refund cannot be rolled back. The key `{TransactionId}:{PaymentBreakdownId}` is stable, so a retry should not refund twice, but this only holds if the real gateway honours the idempotency key. A failure after some card refunds succeeded is logged as an error for manual reconciliation.
- **30 s timeout:** applied as the total budget of one refund call, retries included, not per attempt. With several card payments the budgets add up (see Concurrency); a total budget for the whole cancellation is not applied because stopping between refunds leaves some cards refunded and others not.
- **Several card refunds:** their references are stored comma-separated in `SaleCancellation.GatewayReference` (up to 500 characters). A dedicated table could replace this if the audit needs it.
- **Mock only in Development (PO review of PR #11):** `UseMock` is `false` in the base `appsettings.json` and in `BankGatewayOptions`, and `true` only in `appsettings.Development.json`. The gateway contract itself is still provisional.
- **Reconciliation log (PO review of PR #11):** if `SaveChangesAsync` or `CommitAsync` fails after the gateway refunded, or the unique index answers `409`, the service logs an error with the folio, refund references and refunded amount, then rethrows (`500`). **PENDING** (PO, review point 2): (a) accept a retry that relies on the gateway's `Idempotency-Key`, (b) add an automatic compensation or a "pending reconciliation" mark, or (c) manual reconciliation from the log is enough. Until decided, (c) is what exists.
- **Race with the shift close (PMC-8/PMC-9):** the cancellation locks the sale but not the `CashShift`. If `CloseAsync` runs while a cancellation waits for the gateway, the close counts the sale as `COMPLETADO` and the sale then becomes `CANCELADO`, so the saved shift shows a false shortage. It is rare. Locking the shift first (same order as `CloseAsync`: shift, then sale) would avoid the deadlock but would freeze that cashier's charges for as long as the gateway takes, so it is left for PMC-9 to decide how the count treats cancelled sales.
- **Database isolation in PMC-9:** its branch enables `READ_COMMITTED_SNAPSHOT` by editing the already applied migration `20261020190000`; databases that already ran it will not have it. `UPDLOCK` works either way, but the locking behaviour will differ per environment. To be noted in that PR.
- **Role name (PMC-16):** decided by the PO: accept both. PMC-16 must use the same list, `Gerente,Administrador,Admin`, so no endpoint rejects the administrator.
- **Cancellation ticket (PMC-14):** the message says "Se generó el ticket de cancelación", but the print DTO has no reason or `fecha_cancelacion`. To confirm with the PO whether printing the ticket with `estado: CANCELADO` is enough.
- **Orders (PMC-6):** `PaymentCompletedEvent` was already published and the cancellation emits no event, so Órdenes keeps seeing the session as paid. Cross-service risk, documented as a decision above.

---

## Real-Time Cash Shift Summary Decisions (PMC-9)

Agreed while implementing PMC-9:

- **Route:** `GET /api/v1/cash-shifts/summary` on `CashShiftsController` follows the English `Code Language` convention (replacing the Spanish `/api/v1/caja/arqueo` from the story; to be aligned with Frontend / API Gateway). Wire JSON property names keep the Spanish snake_case contract (`turno_caja_id`, `estado_turno`, `fondo_inicial`, `ingresos.total_efectivo_cobrado`, `ingresos.total_tarjetas`, `total_esperado_en_caja`, `fecha_calculo`).
- **Identity & no open shift:** the cashier ID is extracted exclusively from the JWT `NameIdentifier` (`sub`) claim (`401 Unauthorized` if missing). If the cashier has no `Open` shift, `GET /api/v1/cash-shifts/summary` returns `404 Not Found` (`ProblemDetails`), as required by PMC-9 Scenario 2.
- **Server-side SQL aggregation (`CalculateShiftTotalsAsync`):** both `CashShiftService.GetSummaryAsync` (PMC-9) and `CashShiftService.CloseAsync` (PMC-8) share the same single atomic server-side LINQ aggregation using `.AsNoTracking()` and conditional `.Sum(...)` over `TransactionStatus.Completed` rows in one query so raw sales by method and tip deductions are evaluated from the exact same statement snapshot, individual transactions are never loaded into memory, and the real-time summary always matches the final close calculation (excluding tips from `total_efectivo_cobrado`, `total_tarjetas`, and `total_esperado_en_caja`).
- **Read Committed Snapshot Isolation (`RCSI`):** `READ_COMMITTED_SNAPSHOT ON` is enabled on the SQL Server database via dedicated migration `20261022120000_EnableReadCommittedSnapshot` and `GetSummaryAsync` queries under ambient Read Committed with statement-level snapshot isolation (no explicit transaction) so concurrent uncommitted sales in progress are ignored without blocking readers or producing dirty reads (PMC-9 Scenario 4).

---

## Print Ticket Decisions (PMC-14)

Confirmed with the story owner (Edwing) while implementing PMC-14:

- **Route:** `GET /api/v1/pagos/{folio}/ticket-impresion`, any authenticated caller (`[Authorize]`). Unknown folio returns `404` (`ProblemDetails`, `detail` `"The ticket '{folio}' does not exist."`, the same English text as PMC-11); database failures are not caught and the global exception handler returns `500` without internals.
- **`fecha_impresion`:** the sale date (`PaymentTransaction.CreatedAt`), not the time of each reprint, so every reprint is identical to the original.
- **Footer:** `subtotal`, `impuestos`, `propina`, `total`, `pagos` and `estado` (subtotal, taxes and tip were added to the story's example).
- **Folio:** the format generated by PMC-6 (`TX-{yyyyMMdd}-{number:D6}`); the story's `TX-10500` is only illustrative.
- **Query:** pure EF projection with `AsNoTracking()` (no `Include().ToList()`), reading only the plain-text history persisted at charge time. No HTTP caching for now, since a later cancellation (PMC-12) changes `estado`.
- **Line and payment order:** `cuerpo` and `pie.pagos` are ordered by `TransactionItem.Id` / `PaymentBreakdown.Id`. `ChargeService` never assigns those ids, so EF Core's SQL Server provider generates them client-side with its sequential GUID generator, in the order the entities are added; that generator is built to sort the same way SQL Server compares `uniqueidentifier`, so `ORDER BY Id` returns the charge order. This is the official ordering mechanism: if any code starts assigning these ids itself (e.g. `Guid.NewGuid()`), add an explicit `Sequence` column set at charge time and order by it instead. Covered by `Lines_and_payments_keep_the_order_they_were_charged_in`.
- **Coexistence with PMC-11:** `GET /api/v1/pagos/{folio}` (`TransactionDetailService`) is the general transaction detail; `GET /api/v1/pagos/{folio}/ticket-impresion` (`PrintTicketService`) is a contract specialised for the ESC/POS printer. Both read the same persisted history, but the queries stay separate on purpose: each projects only the columns its own DTO needs and the two contracts evolve for different consumers (Frontend vs. printer). Neither endpoint calls the catalogue, orders or any other microservice. PMC-14 does not change the PMC-11 contract.
- **Tests:** `PrintTicketTests` (service and controller) and `PrintTicketHttpTests` (real HTTP pipeline: `401` without token, `500` without internals on a database timeout, JSON contract as serialized by MVC) run against SQL Server: the former in the shared `payments_tests` database, the latter in `payments_tests_http`.
- **HTTP integration tests:** `PaymentsApiFactory` (`WebApplicationFactory<Program>`, shared via `IClassFixture`) boots `Program.cs` in the `Testing` environment and swaps only what a test run lacks: the Gateway JWT for `TestAuthHandler` (`X-Test-User` / `X-Test-Roles` headers; no header = anonymous), RabbitMQ for MassTransit's in-memory test harness, and the database for a SQL Server test database of its own (`payments_tests_http`, migrated once by `PaymentsApiFactory.EnsureDatabaseAsync()` before the app starts, so hosted services such as the MassTransit outbox delivery never touch the database other suites use in parallel) with `FailingQueries`, which fails like a timeout any query receiving a chosen parameter value (e.g. a folio). Reuse it for other endpoints instead of building another factory.

---

## General Rules

- **Code Formatting:** All code contributions must adhere to repository standards and pass `dotnet format --verify-no-changes`.
- **Code Language:** All code is written in English: identifiers, types, methods, variables, test names, log messages, comments, and cache keys. No Spanish and no Spanglish. When a wire contract with another service uses Spanish field names, keep the English property and map the name with `[JsonPropertyName]`.
- **Commit Messages:** Must follow the [Conventional Commits](https://www.conventionalcommits.org/) specification (`feat:`, `fix:`, `chore:`, `refactor:`, `test:`, `docs:`).
- **Scope Discipline:** Do not perform unrelated refactoring or edit files outside the requested scope.
- **Frontend Compatibility:** Maintain backward compatibility for API contracts consumed by the Frontend repository (via the API Gateway) unless breaking changes are explicitly requested.

---

## Workflow Protocol

Agents collaborating on this repository follow a structured 3-phase lifecycle tracked in `.agents/current/`:

### 1. Analysis & Planning Phase (`analyst`)
- Save the incoming task description to `.agents/current/request.md`.
- Inspect existing Controllers, Services, Models, and routes.
- Formulate the minimal implementation plan in `.agents/current/plan.md`.
- Generate explicit, file-specific task instructions in `.agents/current/editor-task.md`.
- **Do not modify application source code during this phase.**

### 2. Execution Phase (`editor`)
- Implement strictly the approved scope defined in `.agents/current/editor-task.md`.
- Touch only the files explicitly designated as writable targets.
- Adhere strictly to the MVC without View separation.
- Document changes in `.agents/current/execution-report.md`.

### 3. Audit & Verification Phase (`auditor`)
- Verify architectural compliance (Controllers -> Services -> Models/Persistence).
- Execute verification commands:
  ```powershell
  dotnet format --verify-no-changes
  dotnet build --configuration Release
  dotnet test --configuration Release
  ```
- Document validation status and findings in `.agents/current/auditor-report.md`.

---

## Agent Role Definitions

Detailed agent role specifications are defined in:

- [Analyst](file:///.agents/agents/analyst/agent.md)
- [Editor](file:///.agents/agents/editor/agent.md)
- [Auditor](file:///.agents/agents/auditor/agent.md)
