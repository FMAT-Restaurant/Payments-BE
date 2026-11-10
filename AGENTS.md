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
- **Pending index (for PMC-27):** every saved snapshot is added to the Redis sorted set `payments:pending_sessions` (`IPendingSessionIndex` / `RedisPendingSessionIndex` over `IConnectionMultiplexer`), scored by the time Pagos saw the change (not the event `Timestamp`, so a late delivery still shows up in a `since` query). Entries older than 24 h are trimmed on each add, matching the snapshot TTL. `ChargeService` removes the session after `COMMIT`; a Redis failure there is logged and does not undo the charge. Tests use `InMemoryPendingSessionIndex` (CI has no Redis).
- **Event display fields:** `tableName`, `waiterName` and `orderFolio` (camelCase on the wire) are optional and nullable until Órdenes publishes them; they are stored with the snapshot and `tableName` is persisted with the transaction.
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
- **Source of truth for the amount due:** the pending bill saved by PMC-5 in Redis; the persisted items come from the bill. The request no longer carries `detalles_snapshot` (removed with the PMC-7 contract). Amount due = `ExpectedTotal + tip`; `Total` = sum of `PaymentBreakdown`.
- **Request contract (PMC-7):** `{ "dinning_session_id", "tip", "payments": [{ "method", "amount", "authorization_reference" }] }`. A card payment without `authorization_reference` answers `422 AUTHORIZATION_REFERENCE_REQUIRED` (`"La referencia de autorización es obligatoria para pagos con tarjeta"`), checked first in `ChargeCalculationService`, before any write.
- **Persisted extras:** `ChangeGiven` and `TableName` (from the snapshot) are stored with the transaction (migration `AddChargeAndShiftDetails`).
- **Change:** payments are capped to the amount owed (electronic first, cash absorbs the excess), so change is never stored as income. Electronic payments exceeding the total are rejected (422).
- **Idempotency:** unique index on `DinningSessionId` plus a prior check; both paths return 409.
- **Event:** `PaymentCompletedEvent` with fixed `[MessageUrn("PaymentCompletedEvent")]`, published through the MassTransit EF **bus outbox** (only delivered after COMMIT). This outbox applies to publishes from services; the PMC-5 consumer keeps its Redis idempotency.
- **Folio:** `TX-{yyyyMMdd}-{number:D6}` where the number restarts every UTC day. It comes from the `FolioCounters` table (one row per day) through `ChargeService.NextDailyNumberAsync`: a `MERGE ... WITH (HOLDLOCK)` with `OUTPUT` inside the charge transaction, so concurrent charges never share a number and a rolled-back charge gives its number back. The global sequence `transaction_number_seq` still feeds `Number` (ordering). The migration seeds each day's counter with the highest number already used that day, so old folios are never repeated.
- **Identity:** JWT Bearer configured in `Authentication:Schemes:Bearer`; the cashier is the `NameIdentifier` (`sub`) claim, never the payload.
- **Cash shift:** `CashShift` has the shape required by PMC-8 (open/close endpoints belong to PMC-8). Charging requires an `Open` shift of the cashier (`403 CASH_SHIFT_NOT_OPEN` otherwise).
- **Tests:** `ChargeServiceTests` run against a real SQL Server (`payments_tests` database; the server comes from the required `ConnectionStrings__SqlServer` env var, so no password is committed). CI uses a SQL Server service container.

---

## Cash Shift Decisions (PMC-8)

Agreed while implementing and reviewing PMC-8:

- **Routes:** `POST /api/v1/payments/cash-shifts/open` and `POST /api/v1/payments/cash-shifts/close`. Wire JSON names are English snake_case per the updated story: request `opening_float`, `declared_cash`, `reason`; response `cash_shift_id`, `status`, `difference`, `message`.
- **Cashier name and close reason:** `CashierName` is taken at opening from the JWT `name` claim (or `ClaimTypes.Name`), pending confirmation with Auth of which claim carries it (null until then). `reason` is optional (max 500), stored trimmed in `CloseReason` (null when blank).
- **Required decimal fields:** `InitialFund` and `DeclaredCash` are `decimal?` on the request DTOs and validated with `.NotNull().GreaterThanOrEqualTo(0m).PrecisionScale(18, 2, true)` so an empty payload `{}` is rejected with `400 Bad Request` instead of defaulting to `0.00`.
- **Open shift concurrency:** a filtered unique index `IX_CashShifts_CashierId` (`WHERE [Status] = 'Open'`) plus a prior check guarantees at most one open shift per cashier; concurrent duplicates return `409 Conflict` (`RowVersion` only protects existing rows on `UPDATE`, whereas the filtered unique index prevents concurrent duplicate `INSERT`s).
- **Close vs. charge concurrency:** both `ChargeService.ChargeAsync` and `CashShiftService.CloseAsync` acquire a row lock on the open `CashShift` (`WITH (UPDLOCK, ROWLOCK, INDEX(IX_CashShifts_CashierId))` and literal `Status = 'Open'`) inside their `ReadCommitted` transaction before reading or inserting `Transactions`. The index hint and `'Open'` literal ensure an index seek that locks only that cashier's open shift without blocking other cashiers. `RowVersion` remains as the optimistic concurrency token on `CashShift`.
- **Reconciliation formula (PMC-8/PMC-9 adecuaciones):** `ExpectedCash = InitialFund + sum(cash PaymentBreakdown)`, i.e. cash received minus change given, tip included (what is physically in the drawer). `CardTotal` sums the card breakdowns (tip included). `TipsTotal` sums `Tip`. This supersedes the earlier rule that excluded tips from `ExpectedCash` and `CardTotal`.
- **Identity & validation conventions:** cashier identity is read via `User.FindFirstValue(ClaimTypes.NameIdentifier)` in the controller rather than injecting `IHttpContextAccessor` into services (keeping Services decoupled from `HttpContext`), and validator classes use English names (`OpenCashShiftRequestValidator`, `CloseCashShiftRequestValidator`).
- **Reconciliation difference & messages:** `Difference = DeclaredCash - ExpectedCash` (negative on shortage, positive on surplus, `0.00` on exact match). Exact match transitions status to `Closed` (`CERRADO`, `"Corte exacto. Turno cerrado."`); non-zero transitions to `Unbalanced` (`DESCUADRADO`, `"Corte con faltante. Turno cerrado con descuadre."` or `"Corte con sobrante. Turno cerrado con descuadre."`).
- **Closing without open shift:** returns `422 CASH_SHIFT_NOT_OPEN` (`ErrorResponse`).
- **Persisted summary:** `CashShift` stores `DeclaredCash`, `ExpectedCash`, `CardTotal`, `TipsTotal`, and `Difference` in the database while the close response returns the 4 contract fields.

---

## Change Calculation Decisions (PMC-13)

Confirmed by the PO (PMC-13-P1 to P9). Most of the behaviour already existed since PMC-6; PMC-13 extracts it into a domain service, aligns messages and covers the scenarios:

- **Domain service:** `IChargeCalculationService` / `ChargeCalculationService` (English names per this file, for the story's `ICalculoCobroDomainService`). It is pure (no I/O, registered as singleton) and returns `ChargeCalculation` with the status (`Covered`, `AuthorizationReferenceRequired`, `InsufficientFunds`, `ElectronicOverpayment`, `CardCoversFullAmount`), the capped `PaymentBreakdown` list, `ChangeGiven` and `Deficit`. `ChargeService` keeps persistence, locking and the outbox.
- **Change:** `change_given` = cash - (amount due - card), where amount due = `ExpectedTotal + tip`. Electronic payments are applied first and never capped; cash absorbs the excess with `Math.Min(amount, remaining)`, so the sum of `PaymentBreakdown` always equals `Total`.
- **Error contract (PO agreement, 2026-11-05):** business errors of `POST` charge return `ErrorResponse` `{ "code": "STRING_ENUM", "message": "Texto en español" }` instead of `ProblemDetails`. `400` and `500` follow the same contract (see Cross-cutting API Contract). This supersedes the earlier decision of English `detail` texts.
- **Codes:** `TRANSACTION_ALREADY_PAID` (409, `"La transacción ya ha sido pagada previamente"`), `BILL_NOT_FOUND` (404), `CASH_SHIFT_NOT_OPEN` (403, `"Debe abrir caja antes de cobrar"`), `INSUFFICIENT_FUNDS` (422, `"El monto de los pagos no cubre el total de la cuenta"`, plus a `deficit` field present only for this code), `CARD_AMOUNT_EXCEEDS_BALANCE` (422, `"Los pagos con tarjeta no pueden exceder el total de la cuenta"`), `CARD_COVERS_FULL_AMOUNT` (422, `"La tarjeta cubre el total; no se admite efectivo adicional"`), `AUTHORIZATION_REFERENCE_REQUIRED` (422, PMC-7).
- **Electronic overpayment (422 `CARD_AMOUNT_EXCEEDS_BALANCE`):** only when electronic payments on their own exceed the amount due. A mixed payment whose cash produces the change (e.g. card 50 + cash 100 over 120) is valid. Checked in the service before any write; no `.Custom()` rule in `ChargeRequestValidator`, so validation errors stay `400`.
- **Card covering the whole bill plus cash (422 `CARD_COVERS_FULL_AMOUNT`):** if electronic payments alone cover the amount due and any cash payment is sent, the charge is rejected before any write. A card-only payment covering the bill is valid with `change_given = 0`.
- **Persisted:** since the PMC-6 adecuaciones `ChangeGiven` is stored with the transaction; the amount received is not (it is `Total + ChangeGiven`). The response field is `change_given`; `estado` and `mensaje` keep the names written in PMC-13.
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

- **Routes:** everything lives under `/api/v1/payments`: `POST /charge`, `POST /cash-shifts/open`, `GET /cash-shifts/open`, `POST /cash-shifts/close`, `GET /cash-shifts/me`, `GET /cash-shifts/summary`, `GET /cash-shifts/{cash_shift_id}/summary`, `GET /` (history), `GET /{folio}` (detail), `GET /{folio}/ticket` (print), `GET /export`. Literal segments (`charge`, `export`, `cash-shifts`) win over `{folio}`. Query parameter names (`inicio`/`fin`, `fecha_inicio`, ...) and JSON field names move to English in each story's own adecuacion.
- **Errors:** every non-2xx body with content is `ErrorResponse` `{ "code", "message" }` with the message in Spanish; the Frontend relies on `code` only.
  - `400 VALIDATION_ERROR`: FluentValidation messages joined in one string (`ErrorResponse.Validation`), built-in texts in Spanish (`LanguageManager.Culture = es`) naming the wire field (`DisplayNameResolver` reads `[JsonPropertyName]`); malformed JSON answers `"La solicitud tiene un formato inválido."` (`InvalidModelStateResponseFactory`).
  - `500 INTERNAL_ERROR`: `UseExceptionHandler` answers `"Ocurrió un error interno. Intente de nuevo más tarde."` without internals.
  - `404 TRANSACTION_NOT_FOUND` (`"El ticket '{folio}' no existe"`) for detail and print; cash shifts answer `404 CASH_SHIFT_NOT_OPEN` (cashier summary), `404 CASH_SHIFT_NOT_FOUND` (`"El turno de caja no existe"`, supervision summary by id), `409 CASH_SHIFT_ALREADY_OPEN` (open) and `422 CASH_SHIFT_NOT_OPEN` (close).
  - `401` and framework-level challenges still have an empty body; `403 FORBIDDEN_ROLE` with `{ code, message }` is returned for unauthorized supervisor access (PMC-31 / PMC-16).

---

## Sales History Decisions (PMC-10 / PMC-24)

Agreed with the PO (05/11/2026) for the Frontend sales history screen:

- **Route:** `GET /api/v1/payments?start_date=&end_date=&page_index=&page_size=`, accessible to any authenticated user (`[Authorize]`). Requests without token return `401 Unauthorized`.
- **Query parameters:** `start_date` and `end_date` are optional UTC dates; `page_index` (default 1, >= 1) and `page_size` (default 10, 1..50).
- **Semi-open date interval `[start_date, end_date)`:** `end_date` is exclusive (the Frontend sends the beginning of the next day in UTC). Dates travel in UTC; the restaurant timezone determines how the FE builds day boundaries.
- **No status or payment method filters:** all returned sales are completed (`Status == TransactionStatus.Completed`), as agreed with the PO.
- **Response contract (200 OK):** returns `PagedResponse<SalesHistoryItem>` in strict snake_case:
  - Metadata: `page_index`, `page_size`, `total_count`, `total_pages`.
  - Items (`data`): array of `{ folio, cash_shift_id, total, status, paid_at, items, payment_breakdowns }`.
  - Lines (`items`): array of `{ dish_name, quantity, unit_price }`.
  - Breakdowns (`payment_breakdowns`): array of `{ method, amount }`.
- **Single query projection (no N+1):** EF Core query with `.AsNoTracking()` directly projects to `SalesHistoryItem`, reading only the persisted snapshot without joins or calls to other microservices.
- **Validation errors (400 Bad Request):** invalid pagination or `start_date >= end_date` returns `400 VALIDATION_ERROR` with message `"La fecha de inicio debe ser anterior a la fecha de fin."` without touching the database.
- **Tests:** unit tests in `SalesHistoryQueryValidatorTests` and `SalesHistoryServiceTests` (`payments_tests_queries`), and HTTP pipeline integration tests in `SalesHistoryHttpTests` (`payments_tests_http`) using `PaymentsApiFactory` covering 200 with date range and pagination, 400 on invalid date range, 500 on timeout without internals, uncommitted transaction isolation (RCSI), and 401 unauthorized.

---

## Transaction Detail by Folio Decisions (PMC-11 / PMC-25)

Agreed with the PO (05/11/2026) for the Frontend transaction detail modal:

- **Route:** `GET /api/v1/payments/{folio}`, accessible to any authenticated user (`[Authorize]`). Requests without token return `401 Unauthorized`.
- **Response contract (200 OK):** returns `TransactionDetailResponse` in strict snake_case with English field names and no cancellation block:
  - Header: `folio`, `cash_shift_id`, `dinning_session_id`, `status` (`COMPLETADO` / `CANCELADO`), `paid_at` (`PaymentTransaction.CreatedAt`), `subtotal`, `taxes`, `tip`, `total`, and `change_given`.
  - Items (`items`): array of `{ dish_name, quantity, unit_price, amount }`.
  - Payments (`payment_breakdowns`): array of `{ method, amount, authorization_reference }`.
- **Single query projection (no N+1):** EF Core query with `.AsNoTracking()` directly projecting to `TransactionDetailResponse` without loading full entities or tracking graphs, preventing circular references and loading header, items, and payment breakdowns in one database roundtrip.
- **Inmutability:** returns strictly the plain-text items, amounts, and payments persisted at charge time (PMC-6); no external calls to catalogue, orders, or other microservices.
- **Ordered lines and payments:** preserves charge order using `.OrderBy(i => i.Id)` and `.OrderBy(p => p.Id)` (sequential GUIDs assigned at charge time).
- **Error contract:**
  - Inexistent folio: `404 TRANSACTION_NOT_FOUND` (`"El ticket '{folio}' no existe"`).
  - Database failure / timeout: unhandled exception triggers rollback and global exception handler returns `500 INTERNAL_ERROR` (`"Ocurrió un error interno. Intente de nuevo más tarde."`) without SQL Server internals.
- **Tests:** unit and SQL tests in `TransactionDetailTests` (`payments_tests_queries`), and HTTP pipeline integration tests in `TransactionDetailHttpTests` using `PaymentsApiFactory` (`payments_tests_http`) covering 200 with full contract, 404 for missing folio, 500 on timeout without internal leak, and 401 unauthorized.

---

## Real-Time Cash Shift Summary Decisions (PMC-9)

Agreed while implementing PMC-9:

- **Route and contract:** `GET /api/v1/payments/cash-shifts/summary`. Flat response in English snake_case per the updated story: `cash_shift_id`, `cashier_name`, `status`, `opened_at`, `opening_float`, `expected_cash`, `card_total`, `card_transaction_count`, `retained_tips`, `calculated_at`.
- **Identity & no open shift:** the cashier ID is extracted exclusively from the JWT `NameIdentifier` (`sub`) claim (`401 Unauthorized` if missing). If the cashier has no `Open` shift, the summary returns `404 CASH_SHIFT_NOT_OPEN` (`ErrorResponse`), as required by PMC-9 Scenario 2.
- **Server-side SQL aggregation (`CalculateShiftTotalsAsync`):** `GetSummaryAsync` (PMC-9) and `CloseAsync` (PMC-8) share one server-side LINQ aggregation over the `PaymentBreakdown` rows of `Completed` transactions (`.AsNoTracking()`, conditional `.Sum(...)`), so the summary always matches the close and no transaction is loaded into memory. `expected_cash = opening_float + cash breakdowns` (cash kept after change), `card_total` and `card_transaction_count` count card breakdowns, and `retained_tips = sum(Tip * cardAmount / Total)` rounded half away from zero (the tip share paid by card that the drawer owes the waiters; formula pending PO confirmation).
- **Read Committed Snapshot Isolation (`RCSI`):** `READ_COMMITTED_SNAPSHOT ON` is enabled on the SQL Server database via dedicated migration `20261022120000_EnableReadCommittedSnapshot` and `GetSummaryAsync` queries under ambient Read Committed with statement-level snapshot isolation (no explicit transaction) so concurrent uncommitted sales in progress are ignored without blocking readers or producing dirty reads (PMC-9 Scenario 4).

---

## Print Ticket Decisions (PMC-14)

Agreed with the PO (05/11/2026) for in-browser receipt printing:

- **Route:** `GET /api/v1/payments/{folio}/ticket`, accessible to any authenticated caller (`[Authorize]`). Requests without token return `401 Unauthorized`.
- **Browser printing (No ESC/POS):** References to thermal ESC/POS commands and cancelled sales status were removed; the Front-End renders and prints the ticket directly from the browser.
- **Flattened response contract (200 OK):** returns `PrintTicketResponse` in strict snake_case:
  - Header data: `restaurant_name` (from configuration `"Restaurant:Name"`, default `"Restaurante FMAT"`), `folio`, `paid_at` (sale date `PaymentTransaction.CreatedAt`, so any reprint is identical to the original), `table_name` (persisted on `PaymentTransaction.TableName`, empty string if null), `cashier_name` (`CashShift.CashierName` display name, falling back to `PaymentTransaction.CashierId`).
  - Lines (`items`): array of `{ dish_name, quantity, unit_price, amount }`.
  - Financial summary: `subtotal`, `taxes`, `tip`, `total`, and `change_given`.
  - Payments (`payments`): array of `{ method, amount, authorization_reference }`.
- **Single query projection (no N+1):** EF Core query with `.AsNoTracking()` directly projecting to `PrintTicketResponse`, reading only the persisted snapshot without joins or calls to other microservices (AC1, AC2).
- **Line and payment order:** `items` and `payments` are ordered by `TransactionItem.Id` / `PaymentBreakdown.Id` (sequential GUIDs assigned at charge time), guaranteeing identical ordering across reprints.
- **Precision (AC5):** Every monetary amount is `decimal(18,2)`.
- **Error contract:**
  - Inexistent or whitespace folio: `404 TRANSACTION_NOT_FOUND` (`"El ticket '{folio}' no existe"`).
  - Database failure / timeout: unhandled exception triggers global exception handler returning `500 INTERNAL_ERROR` (`"Ocurrió un error interno. Intente de nuevo más tarde."`) without SQL Server internals.
- **Concurrent reprints:** Concurrent requests for the same ticket return identical 200 OK responses without blocking or contention.
- **Tests:** unit tests in `PrintTicketTests` (`payments_tests`) and HTTP integration tests in `PrintTicketHttpTests` (`payments_tests_http`) using `PaymentsApiFactory` covering 200 with full contract, 404 for missing/whitespace folio, 500 on timeout without internal leak, 401 unauthorized, and 5 concurrent requests without blocking.

---

## Sales Export Decisions (PMC-15)

Agreed with the PO (05/11/2026) for accounting sales export:

- **Route and access:** `GET /api/v1/payments/export?start_date=...&end_date=...` (ISO 8601), restricted to `[Authorize(Roles = "Administrador")]`. Requests without token return `401 Unauthorized`; other roles return `403 Forbidden`. The API Gateway issues `Administrador` and cashier roles only; there is no `Contador` role.
- **Window:** filters by sale date (`PaymentTransaction.CreatedAt`) as a semi-open window `[start_date, end_date)`. `end_date` is exclusive (the FE sends the next day boundary in UTC). `start_date` and `end_date` are mandatory, `end_date > start_date`, and the range cannot exceed 31 days (`SalesExportQueryValidator`); violations return `400 VALIDATION_ERROR` with message in Spanish (e.g. `"El rango de fechas no puede exceder los 31 días."`) before touching the database. Also supports `inicio`/`fin` query parameters for compatibility.
- **Rows & status:** all sales in the window are completed; there is no filter by cancelled status and the `Estado` column is removed from the CSV contract.
- **Columns:** `Folio,Fecha,Subtotal,Impuestos,Propina,Total,Efectivo,Tarjetas`. `Fecha` is UTC ISO 8601 (`yyyy-MM-ddTHH:mm:ssZ`); amounts are `decimal` with invariant `0.00` format; `Subtotal`, `Impuestos`, and `Propina` come from persisted fields; `Efectivo` sums `Cash` breakdowns and `Tarjetas` sums `CreditCard` + `DebitCard` in SQL.
- **Filename:** `Content-Disposition: attachment; filename="reporte_ventas_{yyyyMMdd}_{yyyyMMdd}.csv"` named after the start day and the last day covered (`end.AddTicks(-1)`).
- **Index:** `IX_Transactions_CreatedAt_Number` serves the date window in query order, seeking instead of scanning `Transactions`.
- **Streaming:** `AsNoTracking().AsAsyncEnumerable()` written with CsvHelper (RFC 4180, UTF-8 without BOM); no `ToList`. Rows are gathered in blocks of 500 in a reusable in-memory buffer and flushed to `Response.Body` in complete blocks (chunked transfer).
- **Failure handling:** if the database fails before sending data, the global exception handler answers `500 INTERNAL_ERROR` without SQL Server internals. If it fails mid-stream, the controller logs the error and aborts the connection via `HttpContext.Abort()` so the download is visibly incomplete.
- **Isolation:** `READ COMMITTED` with RCSI, so uncommitted sales in progress are never read and exports never block concurrent charges.
- **Tests:** `SalesExportTests` (`payments_tests`) and `SalesExportHttpTests` (`payments_tests_http`) using `PaymentsApiFactory` covering 200 with full 8-column contract, 401 unauthorized, 403 forbidden, 400 for range > 31 days or missing dates, 500 on timeout before start, mid-stream abort, and uncommitted sale isolation.

---

## Supervision Cash Shift Summary by Id Decisions (PMC-32 / PMC-16)

Agreed while implementing the supervision cash shift summary by id:

- **Route and roles:** `GET /api/v1/payments/cash-shifts/{cash_shift_id}/summary` is restricted to supervisor roles (`Administrador` and `Gerente`). A cashier (`Cajero`) or any role without supervision permissions receives `403 FORBIDDEN_ROLE` with error body `{ "code": "FORBIDDEN_ROLE", "message": "No tiene permisos para realizar esta acción." }`. A request without a token returns `401 Unauthorized`. Caller identity is extracted exclusively from the JWT claims forwarded by the API Gateway; user IDs are never accepted in headers or request bodies.
- **Input validation:** `cash_shift_id` must be a valid, non-empty GUID. Non-GUID strings (e.g. `abc`) or empty GUIDs (`00000000-0000-0000-0000-000000000000`) return `400 VALIDATION_ERROR` with message `"El id del turno de caja tiene un formato inválido."` before touching the database.
- **Non-existent shift:** when no cash shift exists with the provided `cash_shift_id`, the endpoint returns `404 CASH_SHIFT_NOT_FOUND` with message `"El turno de caja no existe"`. The error response helper `ErrorResponse.CashShiftNotFound()` is added to the shared error catalogue.
- **Closed and unbalanced shifts:** if the shift is already closed (`CERRADO` / `CashShiftStatus.Closed` or `DESCUADRADO` / `CashShiftStatus.Unbalanced`), the endpoint returns `200 OK` with the final arqueo of that shift and its corresponding status (PO decision, replacing the proposed `409 CASH_SHIFT_CLOSED`).
- **Calculation reuse and non-blocking reads (RCSI):** reuses the same SQL aggregation (`CalculateShiftTotalsAsync`) shared between PMC-9 and PMC-8 close reconciliation. Calculates `expected_cash = initial_fund + total_cash` (with card totalized separately and tips kept in drawer cash). Pure read-only projection (`AsNoTracking()`) under statement-level snapshot isolation (RCSI), ensuring it does not lock or block concurrent charges in progress on the queried shift.
- **Parity and coherence with PMC-9:** for an active open shift, the owner cashier calling `GET /api/v1/payments/cash-shifts/summary` and a supervisor calling `GET /api/v1/payments/cash-shifts/{cash_shift_id}/summary` receive identical financial amounts in the shared response contract `CashShiftSummaryResponse`.
- **Tests:** unit tests in `CashShiftServiceTests` and HTTP pipeline integration tests in `CashShiftSummaryByIdHttpTests` using `PaymentsApiFactory` (`payments_tests_http`), covering open shifts (200), closed shifts (200), unbalanced shifts (200), non-existent shifts (404), cashier forbidden (403), unauthorized (401), invalid GUID format (400), and full parity with PMC-9.

---

## Active Operational Cash Shift Decisions (HU-FE-05 / PMC-8)

Agreed while implementing the active operational shift query for the logged-in user:

- **Route and access:** `GET /api/v1/payments/cash-shifts/me` is protected by `[Authorize]` and accessible to `Administrador`, `Gerente`, and `Cajero`. Requests without a token return `401 Unauthorized`. Callers with unauthorized roles return `403 FORBIDDEN_ROLE` with error response body `{ "code": "FORBIDDEN_ROLE", "message": "No tiene permisos para realizar esta acción." }`.
- **Strict identity extraction:** user ID is never accepted as a path/query parameter or in the body, nor from client headers (`X-User-Id`). Identity is resolved exclusively from the JWT claim (`NameIdentifier` / `sub`), returning `401` if missing.
- **Open shift response (200 OK):** if the user has an active shift (`Status = 'Open'`), returns `ActiveCashShiftResponse` in strict snake_case:
  `{ "cash_shift_id": "uuid", "opened_at": "ISO-8601", "opening_float": 1000.00 }`.
- **No open shift response (404 Not Found):** if the user has no shift or only closed/unbalanced shifts, returns `404 Not Found` with `{ "code": "CASH_SHIFT_NOT_OPEN", "message": "No hay un turno de caja abierto para este usuario" }`. The Frontend intercepts this status code to trigger the modal for opening a shift and declaring initial cash without receiving generic 500 errors.
- **Integrity and concurrency:** guaranteed by the filtered unique index `IX_CashShifts_CashierId` (`WHERE [Status] = 'Open'`), ensuring a cashier cannot have more than one open shift concurrently. Read queries use `AsNoTracking()` under statement-level snapshot isolation (RCSI).
- **Tests:** unit tests in `CashShiftServiceTests` and HTTP integration tests in `ActiveCashShiftHttpTests` covering 200 with snake_case contract, 404 for closed/inexistent shifts, 401 without token, 403 on unauthorized roles, and strict JWT extraction ignoring query params and client headers.

---

## Open Cash Shifts for Supervision Decisions (PMC-30 / HU-FE-09)

Agreed while implementing the open cash shifts listing for supervision:

- **Route and roles:** `GET /api/v1/payments/cash-shifts/open` is protected with `[Authorize]` and exclusively restricted to supervisor roles (`Administrador` and `Gerente`). Requests without token return `401 Unauthorized`. Callers with unauthorized roles (such as `Cajero`) receive `403 FORBIDDEN_ROLE` with error response body `{ "code": "FORBIDDEN_ROLE", "message": "No tiene permisos para realizar esta acción." }`.
- **Query performance & lightness:** selects from `CashShifts` filtering strictly by `Status == CashShiftStatus.Open` using `.AsNoTracking()`. Does not calculate real-time sales or totals (delegated to individual summary endpoints). Orders results chronologically by opening time ascending (`OrderBy(s => s.OpenedAt)`).
- **Response contract (200 OK):** returns a JSON array in strict snake_case:
  `[ { "cash_shift_id": "...", "cashier_name": "...", "opened_at": "...", "opening_float": 1000.00 } ]`. If no open shifts exist, returns `200 OK` with an empty array `[]`.
- **Coexistence with shift open POST:** `GET /cash-shifts/open` coexists cleanly with `POST /cash-shifts/open` on the same route distinguished by HTTP verb.
- **Tests:** unit tests in `CashShiftServiceTests` and HTTP pipeline integration tests in `OpenCashShiftsHttpTests` using `PaymentsApiFactory` (`payments_tests_http`), verifying 200 with chronological ordering, empty list when none open, 403 on `Cajero`, 401 without token, and snake_case DTO contract.

---

## Force Close Cash Shift for Supervision Decisions (PMC-16 / HU-FE-09)

Agreed while implementing the forced closure of cash shifts by supervision:

- **Route and roles:** `POST /api/v1/payments/cash-shifts/{cash_shift_id}/force-close` is protected by `[Authorize]` and exclusively accessible to supervisor roles (`Administrador` and `Gerente`). Requests without a token return `401 Unauthorized`. Requests from unauthorized roles (such as `Cajero`) are rejected with `403 FORBIDDEN_ROLE` (`{ "code": "FORBIDDEN_ROLE", "message": "No tiene permisos para realizar esta acción." }`) before business logic execution.
- **Strict identity and audit trail:** the supervisor identity is extracted strictly from the JWT claim (`NameIdentifier` / `sub`). It is persisted on `CashShift.ClosedByAdminId` (nvarchar(128)) together with `CashShift.ForceCloseReason` (nvarchar(500)), providing an immutable audit trail.
- **Input validation (FluentValidation):** `declared_cash` is required, >= 0 with decimal precision (18, 2); `force_close_reason` is required and must have a minimum length of 10 characters. Route parameter `{cash_shift_id}` is validated as a non-empty GUID. Validation failures return `400 VALIDATION_ERROR` with Spanish messages.
- **Calculation reuse and locking:** reuses the same SQL aggregation (`CalculateShiftTotalsAsync`) shared with PMC-9, calculating `expected_cash = initial_fund + cash_sales`, `card_transaction_count`, and `retained_tips`. Row-level lock (`UPDLOCK, ROWLOCK`) on `CashShifts` inside a `ReadCommitted` transaction ensures serialization against concurrent sales or close operations without blocking other shifts.
- **State transitions and messages:** compares `declared_cash` against `expected_cash`:
  - Exact match (`difference == 0m`): transitions status to `Closed` (`CERRADO`) with message `"Cierre forzado ejecutado exitosamente con corte exacto."`.
  - Shortage (`difference < 0m`): transitions status to `Unbalanced` (`DESCUADRADO`) with message `"Cierre forzado ejecutado exitosamente con faltante de caja."`.
  - Surplus (`difference > 0m`): transitions status to `Unbalanced` (`DESCUADRADO`) with message `"Cierre forzado ejecutado exitosamente con sobrante de caja."`.
- **Response contract (200 OK):** returns `ForceCloseCashShiftResponse` in strict snake_case:
  `cash_shift_id`, `cashier_name`, `opened_at`, `closed_at`, `status`, `opening_float`, `expected_cash`, `declared_cash`, `difference`, `card_transaction_count`, `retained_tips`, and `message`.
- **Error catalog & infrastructure resilience:**
  - Non-existent shift: `404 CASH_SHIFT_NOT_FOUND` (`"El turno de caja no existe"`).
  - Already closed shift: `409 CASH_SHIFT_CLOSED` (`"El turno de caja ya ha sido cerrado"`).
  - Database failure / timeout: automatic rollback retains the shift in `Open` (`ABIERTO`) status and global exception handler returns `500 INTERNAL_ERROR` without leaking internals.
- **Tests:** unit tests in `CashShiftServiceTests` and HTTP pipeline integration tests in `ForceCloseCashShiftHttpTests` using `PaymentsApiFactory` (`payments_tests_http`), verifying all 4 Gherkin scenarios: happy path (shortage, exact, surplus), RBAC 403 / 401, non-existent 404, already closed 409, validation 400, and DB failure rollback 500.

---

## Pending Bills for Cashier Inbox Decisions (HU-FE-02 / PMC-5)

Decisions for cashier inbox pending accounts and payment screen reload:

- **Routes and RBAC:**
  - `GET /api/v1/payments/pendings`: returns all active pending accounts for the initial cashier inbox.
  - `GET /api/v1/payments/pendings/{dinning_session_id}`: returns a single pending session for screen reload / rehydration (F5).
  - Access: restricted to `[Authorize(Roles = "Administrador,Gerente,Cajero")]`. Requests without token return `401 Unauthorized`; unauthorized roles return `403 Forbidden`.
- **Data source & secondary Redis index (No SQL, No `KEYS *`):**
  - Endpoints do not query the SQL database; all data is read directly from Redis snapshots.
  - Maintained via a secondary atomic Redis sorted set index `payments:pending_sessions` (`IPendingSessionIndex`) populated by `DinningSessionReadyConsumer` / `PendingBillService.SaveSnapshotAsync`, strictly avoiding blocking `KEYS *` scans.
  - When a bill is successfully paid, `ChargeService.ChargeAsync` removes the snapshot and cleans up the session ID from the active index (`PendingBillService.RemoveAsync`).
  - If no pending bills exist, `GET /api/v1/payments/pendings` returns `200 OK` with an empty array `[]`.
- **Response contract (200 OK):**
  - Strictly English wire property names in `snake_case`.
  - Header: `dinning_session_id`, `order_folio`, `table_name`, `waiter_name`, `timestamp`, `subtotal`, `taxes`, and `total`.
  - Items (`items`): array of `{ dish_id, name, quantity, unit_price, amount, notes }`.
  - Table and waiter names (`table_name`, `waiter_name`) and order folio are resolved directly from the Redis snapshot without extra calls to other services.
  - Financial precision: `decimal(18,2)` native precision.
- **Error handling:**
  - Non-existent or already paid session: `404 SESSION_NOT_FOUND` (`"La cuenta solicitada no existe o ya fue procesada."`).
  - Redis cache failure / timeout: `503 REDIS_TIMEOUT` (`"El servicio de caché no se encuentra disponible temporalmente."`).
  - Invalid GUID format on route parameter: `400 VALIDATION_ERROR` (`"El id de sesión tiene un formato inválido."`).
- **Tests:** unit tests in `PendingBillServiceTests` and HTTP pipeline integration tests in `PendingBillsHttpTests` using `PaymentsApiFactory` (`payments_tests_http`), covering full list (200), empty list (200), single account rehydration (200), 404 for paid/missing account, 503 for Redis timeout, 400 for invalid GUID, and RBAC (401, 403, and 200 for Cajero/Gerente/Administrador).

---

## Cashier Inbox Polling Synchronization Decisions (PMC-27 / HU-FE-02)

Decisions for high-performance short-polling synchronization of the cashier inbox:

- **Strategy & Latency (AC1, AC2):** WebSockets and SSE are explicitly discarded in favor of recurring HTTP Short-Polling (e.g. every 10 seconds). The endpoint relies exclusively on Redis snapshots (0 SQL queries), maintaining response latencies consistently under 50 ms across concurrent cashier requests.
- **Payload optimization via `since` parameter (AC3):** `GET /api/v1/payments/pendings?since={ISO-8601_timestamp}` accepts an optional timestamp query parameter. When present, the service queries the Redis sorted set (`ZRANGEBYSCORE`) to filter active sessions and returns only those created or modified on or after that timestamp (`timestamp >= since`), lightening network bandwidth. If omitted, the full active list is returned (`ZRANGE`).
- **Index & Redis performance:** the Redis sorted set `payments:pending_sessions` associates each session ID with its modification timestamp (Unix ms score), allowing rapid filtering and selective reading of only updated snapshots without full key scans (`KEYS *`). 24-hour safety TTL prevents ghost accounts on unsynchronized cancellations.
- **Implicit account removal (PMC-28 / AC4):** when a bill is successfully charged or cancelled, it is purged from Redis and the active index (`PendingBillService.RemoveAsync`). The endpoint sends no deletion payloads; the Frontend infers removal when the session disappears from subsequent polling responses.
- **Input validation:** unparseable `since` dates return `400 VALIDATION_ERROR` (`"La solicitud tiene un formato inválido."`) before querying cache.
- **Tests:** unit tests in `PendingBillServiceTests` and HTTP integration tests in `PendingBillsHttpTests` validating `?since=` timestamp filtering, 10 concurrent requests without locks and sub-50ms latency, omission of paid/cancelled accounts on next poll, and 400 validation on invalid date strings.

---

## Ghost Account Cleanup by Floor Cancellation Decisions (PMC-28 / HU-FE-02)

Decisions for automatic cleanup of cancelled dining sessions in Redis to prevent ghost accounts in the cashier inbox:

- **Message contract (AC1):** `DinningSessionCancelledEvent` consumed via MassTransit with fixed URN `[MessageUrn("DinningSessionCancelledEvent")]`. Emitted by Órdenes/Piso with fields `dinning_session_id` (and optional `event_id`, `timestamp`, `reason`). `DinningSessionCancelledEventValidator` validates non-empty `dinning_session_id`.
- **Cache cleanup (AC2):** upon receipt, `DinningSessionCancelledConsumer` / `PendingBillService.CancelSessionAsync` deletes the pending bill snapshot (`payments:bill:{dinningSessionId}`) and purges the ID from the active polling secondary index (`payments:pending_sessions`).
- **No WebSockets / Polling reflection (AC3):** no push notifications (WebSocket/SSE) are emitted. The removal in Redis causes the session to naturally disappear from subsequent cashier polling (`GET /api/v1/payments/pendings`), preventing any charge attempt on cancelled tables.
- **Idempotency & duplicates (AC4):** if a cancellation event arrives for a non-existent, already cancelled, or already charged session, the consumer logs an informational message (`"Sesión {SessionId} no encontrada para limpieza, se ignora"`), silently acknowledges (ACK) the message, and exits cleanly without error or exception.
- **Out-of-order handling:** sets a tombstone key (`payments:cancelled:{dinningSessionId}`) with 24 h TTL in Redis. If a late creation event (`DinningSessionReadyEvent`) arrives after cancellation, `PendingBillService.SaveSnapshotAsync` detects the tombstone and discards the creation snapshot.
- **Resilience & DLQ (AC5):** Redis connection or timeout failures throw exceptions, activating MassTransit automatic retries (3 retries with 5 s intervals) and routing unrecoverable failures to `<endpoint>_error` (DLQ) without losing messages. Schema/validation failures bypass retries and route directly to the error queue.
- **Tests:** tests in `DinningSessionCancelledConsumerTests` using `MassTransitTestHarness` covering successful removal from cache and index, idempotent non-existent/duplicate handling, Redis failure triggering retry/DLQ, out-of-order cancellation before creation, and GUID validation.

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
