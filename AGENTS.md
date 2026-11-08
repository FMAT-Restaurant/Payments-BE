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

## Change Calculation Decisions (PMC-13)

Confirmed by the PO (PMC-13-P1 to P9). Most of the behaviour already existed since PMC-6; PMC-13 extracts it into a domain service, aligns messages and covers the scenarios:

- **Domain service:** `IChargeCalculationService` / `ChargeCalculationService` (English names per this file, for the story's `ICalculoCobroDomainService`). It is pure (no I/O, registered as singleton) and returns `ChargeCalculation` with the status (`Covered`, `InsufficientFunds`, `ElectronicOverpayment`), the capped `PaymentBreakdown` list, `ChangeGiven` and `Deficit`. `ChargeService` keeps persistence, locking and the outbox.
- **Change:** `cambio_entregado` = cash - (amount due - card), where amount due = `ExpectedTotal + propina`. Electronic payments are applied first and never capped; cash absorbs the excess with `Math.Min(amount, remaining)`, so the sum of `PaymentBreakdown` always equals `Total`.
- **Error contract (PO agreement, 2026-11-05):** business errors of `POST` charge return `ErrorResponse` `{ "code": "STRING_ENUM", "message": "Texto en español" }` instead of `ProblemDetails`. `400` and `500` follow the same contract (see Cross-cutting API Contract). This supersedes the earlier decision of English `detail` texts.
- **Codes:** `TRANSACTION_ALREADY_PAID` (409, `"La transacción ya ha sido pagada previamente"`), `BILL_NOT_FOUND` (404), `CASH_SHIFT_NOT_OPEN` (403, `"Debe abrir caja antes de cobrar"`), `INSUFFICIENT_FUNDS` (422, `"El monto de los pagos no cubre el total de la cuenta"`, plus a `deficit` field present only for this code), `CARD_AMOUNT_EXCEEDS_BALANCE` (422, `"Los pagos con tarjeta no pueden exceder el total de la cuenta"`), `CARD_COVERS_FULL_AMOUNT` (422, `"La tarjeta cubre el total; no se admite efectivo adicional"`).
- **Electronic overpayment (422 `CARD_AMOUNT_EXCEEDS_BALANCE`):** only when electronic payments on their own exceed the amount due. A mixed payment whose cash produces the change (e.g. card 50 + cash 100 over 120) is valid. Checked in the service before any write; no `.Custom()` rule in `ChargeRequestValidator`, so validation errors stay `400` and the untrusted `detalles_snapshot` is not used for amounts.
- **Card covering the whole bill plus cash (422 `CARD_COVERS_FULL_AMOUNT`):** if electronic payments alone cover the amount due and any cash payment is sent, the charge is rejected before any write. A card-only payment covering the bill is valid with `cambio_entregado = 0`.
- **Not persisted:** neither the change nor the amount received is stored; they are computed in memory and only returned in the response. No entity or migration changes (PMC-6 adecuaciones will persist `ChangeGiven`).
- **Already paid (409):** any previous transaction of the session, including `CANCELADO`, answers `409 TRANSACTION_ALREADY_PAID`.
- **Frontend impact:** the body of the `403`/`404`/`409`/`422` responses changed shape (`ErrorResponse`) and a missing open shift is now `403` instead of `422`. The Frontend must rely on `code` (and `deficit`), never on `message`.
- **Database failure (500):** the `IDbContextTransaction` is rolled back and `UseExceptionHandler` answers `500` `INTERNAL_ERROR` without internals.
- **Precision (RNF-05):** every amount in the calculation, its result and `ChargeResponse.ChangeGiven` is `decimal`; no `float` or `double`.
- **Tests:** `ChargeCalculationServiceTests` (pure, no database) and `ChangeCalculationTests` (real SQL Server, one test per Gherkin scenario; the `500` is checked through a real `TestServer` pipeline because calling the controller directly skips `UseExceptionHandler`).

**PENDING:**

- **Frontend:** has to be told about the new error body and the `403` before PR #13 reaches `qa`.

---

## Cross-cutting API Contract (PO agreement, 2026-11-05)

Applies to every endpoint and supersedes the routes and `ProblemDetails` bodies mentioned in the story sections above and below:

- **Routes:** everything lives under `/api/v1/payments`: `POST /charge`, `POST /cash-shifts/open`, `POST /cash-shifts/close`, `GET /cash-shifts/summary`, `GET /cash-shifts/{cash_shift_id}/summary`, `GET /` (history), `GET /{folio}` (detail), `GET /{folio}/ticket` (print), `GET /export`. Literal segments (`charge`, `export`, `cash-shifts`) win over `{folio}`. Query parameter names (`inicio`/`fin`, `fecha_inicio`, ...) and JSON field names move to English in each story's own adecuacion.
- **Errors:** every non-2xx body with content is `ErrorResponse` `{ "code", "message" }` with the message in Spanish; the Frontend relies on `code` only.
  - `400 VALIDATION_ERROR`: FluentValidation messages joined in one string (`ErrorResponse.Validation`), built-in texts in Spanish (`LanguageManager.Culture = es`) naming the wire field (`DisplayNameResolver` reads `[JsonPropertyName]`); malformed JSON answers `"La solicitud tiene un formato inválido."` (`InvalidModelStateResponseFactory`).
  - `500 INTERNAL_ERROR`: `UseExceptionHandler` answers `"Ocurrió un error interno. Intente de nuevo más tarde."` without internals.
  - `404 TRANSACTION_NOT_FOUND` (`"El ticket '{folio}' no existe"`) for detail and print; cash shifts answer `404 CASH_SHIFT_NOT_OPEN` (cashier summary), `404 CASH_SHIFT_NOT_FOUND` (`"El turno de caja no existe"`, supervision summary by id), `409 CASH_SHIFT_ALREADY_OPEN` (open) and `422 CASH_SHIFT_NOT_OPEN` (close).
  - `401` and framework-level challenges still have an empty body; `403 FORBIDDEN_ROLE` with `{ code, message }` is returned for unauthorized supervisor access (PMC-31 / PMC-16).

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
- **HTTP integration tests:** `PaymentsApiFactory` (`WebApplicationFactory<Program>`, shared via `IClassFixture`) boots `Program.cs` in the `Testing` environment and swaps only what a test run lacks: the Gateway JWT for `TestAuthHandler` (`X-Test-User` / `X-Test-Roles` headers; no header = anonymous), RabbitMQ for MassTransit's in-memory test harness, Redis for an in-memory `IDistributedCache` (so a test can seed a pending bill through `PendingBillService`), and the database for a SQL Server test database of its own (`payments_tests_http`, migrated once by `PaymentsApiFactory.EnsureDatabaseAsync()` before the app starts, so hosted services such as the MassTransit outbox delivery never touch the database other suites use in parallel) with `FailingQueries`, which fails like a timeout any query receiving a chosen parameter value (e.g. a folio). Reuse it for other endpoints instead of building another factory.

---

## Sales Export Decisions (PMC-15)

Taken while implementing PMC-15; pending confirmation with the story owner where noted:

- **Route and access:** `GET /api/v1/pagos/exportar?inicio=...&fin=...` (ISO 8601), restricted to `[Authorize(Roles = "Administrador")]`. Confirmed with Edwing (2026-10-28): the Gateway issues `Administrador` and cashier roles only, there is no `Contador` role, and cashiers do not export the accounting report.
- **Window:** filters by the sale date (`PaymentTransaction.CreatedAt`), as a half-open window `[inicio, fin)` (agreed with Edwing, 2026-10-28): `CreatedAt` keeps fractions of a second, so an inclusive `fin=...23:59:59Z` would drop a sale at `23:59:59.400` from two consecutive reports. A month is requested as `inicio=2026-09-01T00:00:00Z&fin=2026-10-01T00:00:00Z`; the file is named after the last day covered (`reporte_ventas_20260901_20260930.csv`). `inicio` and `fin` are required, `fin > inicio` (with an exclusive `fin`, equal values would always be an empty window), and the range cannot exceed 31 days (`SalesExportQueryValidator`); violations return `400` (`ValidationProblemDetails` keyed `inicio` / `fin`) before any database access. Messages are in English by PO decision (e.g. `"The date range cannot exceed 31 days."`), although scenario 2 of the story writes it in Spanish.
- **Rows:** every sale in the window, cancelled ones included (`Estado` = `COMPLETADO` / `CANCELADO`), per scenario 4 of the story.
- **Columns:** `Folio,Fecha,Estado,Subtotal,Impuestos,Propina,Total,Efectivo,Tarjetas`. `Fecha` is UTC ISO 8601 (`yyyy-MM-ddTHH:mm:ssZ`); amounts are `decimal` with invariant `0.00` format; `Efectivo` sums `Cash` breakdowns and `Tarjetas` sums `CreditCard` + `DebitCard`, both in SQL. The folio is exported as stored by `ChargeService`.
- **Index:** `IX_Transactions_CreatedAt_Number` (migration `AddTransactionsCreatedAtIndex`) serves the sale-date window in the same order as the query, so the export seeks instead of scanning `Transactions`. Agreed with Edwing in review; with RCSI (PMC-9) readers no longer block charges, so the index is about volume, not deadlocks.
- **Streaming:** `AsNoTracking().AsAsyncEnumerable()` written with CsvHelper (RFC 4180, UTF-8 without BOM); no `ToList`. Rows are gathered in blocks of 500 in a reusable in-memory buffer and each complete block is written to `Response.Body` and flushed in one go (Kestrel sends the body chunked). The CSV/text writers never write to the response directly, because they empty their own buffers in the middle of a record.
- **Failure mid-stream:** the controller logs the error and calls `HttpContext.Abort()` so the client gets an incomplete download, never a file that looks finished. What the client received is always whole blocks of lines: the block being built when the database drops is discarded, never half written right before the abort. A failure before the response starts goes to the global exception handler (`500`): `WriteCsvAsync` reads the first row before anything else and the response does not start until the first block of 500 rows is complete, so a failure opening the query or within the first block leaves with zero bytes sent.
- **Isolation:** the default SQL Server `READ COMMITTED` level, so uncommitted sales are never read. No calls to other microservices during the export.
- **Tests:** `SalesExportTests` run against the same SQL Server test database, each test in its own date window. `SalesExportHttpTests` go through the real HTTP pipeline with `PaymentsApiFactory` (`payments_tests_http`): `200` for `Administrador`, `401` without token, `403` for a cashier role, `400` with the English message, `500` without internals when the query fails before anything is sent, and an aborted download when it fails after the first chunk (`FailingQueries.FailReadsAfter`).

---

## Supervision Cash Shift Summary by Id Decisions (PMC-31 / PMC-16)

Agreed while implementing the supervision cash shift summary by id:

- **Route and roles:** `GET /api/v1/payments/cash-shifts/{cash_shift_id}/summary` is restricted to supervisor roles (`Administrador`, `Gerente`, and `Admin`). A cashier (`Cajero`) or any role without supervision permissions receives `403 FORBIDDEN_ROLE` with error body `{ "code": "FORBIDDEN_ROLE", "message": "No tiene permisos para realizar esta acción." }`. A request without a token returns `401 Unauthorized`. Caller identity is extracted exclusively from the JWT claims forwarded by the API Gateway; user IDs are never accepted in headers or request bodies.
- **Input validation:** `cash_shift_id` must be a valid, non-empty GUID. Non-GUID strings (e.g. `abc`) or empty GUIDs (`00000000-0000-0000-0000-000000000000`) return `400 VALIDATION_ERROR` with message `"El id del turno de caja tiene un formato inválido."` before touching the database.
- **Non-existent shift:** when no cash shift exists with the provided `cash_shift_id`, the endpoint returns `404 CASH_SHIFT_NOT_FOUND` with message `"El turno de caja no existe"`. The error response helper `ErrorResponse.CashShiftNotFound()` is added to the shared error catalogue.
- **Closed and unbalanced shifts:** if the shift is already closed (`CERRADO` / `CashShiftStatus.Closed` or `DESCUADRADO` / `CashShiftStatus.Unbalanced`), the endpoint returns `200 OK` with the final arqueo of that shift and its corresponding status (PO decision, replacing the proposed `409 CASH_SHIFT_CLOSED`).
- **Calculation reuse and non-blocking reads (RCSI):** reuses the same SQL aggregation (`CalculateShiftTotalsAsync`) shared between PMC-9 and PMC-8 close reconciliation. Calculates `expected_cash = initial_fund + efectivo recibido − change_given` (with card totalized separately and tips deducted from drawer cash). Pure read-only projection (`AsNoTracking()`) under statement-level snapshot isolation (RCSI), ensuring it does not lock or block concurrent charges in progress on the queried shift.
- **Parity and coherence with PMC-9:** for an active open shift, the owner cashier calling `GET /api/v1/payments/cash-shifts/summary` and a supervisor calling `GET /api/v1/payments/cash-shifts/{cash_shift_id}/summary` receive identical financial amounts in the shared response contract `CashShiftSummaryResponse`.
- **Tests:** unit tests in `CashShiftServiceTests` and HTTP pipeline integration tests in `CashShiftSummaryByIdHttpTests` using `PaymentsApiFactory` (`payments_tests_http`), covering open shifts (200), closed shifts (200), unbalanced shifts (200), non-existent shifts (404), cashier forbidden (403), unauthorized (401), invalid GUID format (400), and full parity with PMC-9.

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
