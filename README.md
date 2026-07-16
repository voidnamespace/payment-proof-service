# Payment Proof

A small .NET 10 service that creates payment operations, durably schedules their
submission to an external provider and accepts the provider's final callback receipt.

The implementation keeps the following invariant: one local operation corresponds to
at most one provider payment, even when submit requests are concurrent, an HTTP response
is lost, or the candidate service is restarted.

## Run

Requirements: Docker with Docker Compose.

```bash
docker compose up --build
```

Services:

- candidate service: `http://localhost:8080`
- provider simulator: `http://localhost:8081`

Readiness check:

```bash
curl -i http://localhost:8080/health
```

## End-to-end scenario

Create an operation:

```bash
curl -i -X POST http://localhost:8080/operations \
  -H "Content-Type: application/json" \
  -d '{"operationId":"operation-123","amount":"1000.00","currency":"RUB","description":"Order payment"}'
```

Durably schedule its submission:

```bash
curl -i -X POST http://localhost:8080/operations/operation-123/submit
```

The first submit returns `202 Accepted`. Repeated submit requests return `200 OK` and do
not create another dispatch intent.

After the simulator sends its callback, inspect the final state and transition history:

```bash
curl -s http://localhost:8080/operations/operation-123
curl -s http://localhost:8080/operations/operation-123/events
```

The final status is either `COMPLETED` or `REJECTED` and is set only by the callback
receipt, never by the provider's HTTP `202` response.

To run the commands repeatedly, use a new `operationId`.

## Persistence check

The SQLite database is stored in the named volume `candidate-data`. Recreate only the
candidate container and query the same operation again:

```bash
docker compose stop candidate-service
docker compose rm -f candidate-service
docker compose up -d candidate-service
curl -s http://localhost:8080/operations/operation-123
```

Do not use `docker compose down -v` when checking persistence because `-v` intentionally
removes the data volume.

## Tests

```bash
dotnet test PaymentProof.slnx
```

The test suite covers validation, duplicate operation IDs, repeated and concurrent
submits, duplicate and conflicting receipts, provider ID mismatches, a lost provider
response, an early callback and recovery of a persisted dispatch intent.

## Design

- SQLite stores operations, transition history, processed receipts and dispatch intents.
- `CREATED -> PROCESSING` and creation of the dispatch intent are committed atomically.
- The external HTTP call is performed after that transaction, without holding a database
  lock.
- Every provider attempt uses `Idempotency-Key` and `X-Correlation-ID` equal to the
  operation ID and sends the same payment body.
- Network failures and HTTP 503 responses leave the operation in `PROCESSING` and are
  retried with exponential backoff and jitter.
- A background worker resumes due intents after startup.
- Duplicate receipts are idempotent. A late opposite receipt is recorded as ignored and
  cannot overwrite the first final status.

## Project layout

- `src/PaymentProof.Api` - API endpoints, persistence, provider client and dispatch worker
- `tests/PaymentProof.Tests` - automated tests
- `compose.yaml` - candidate service, official provider simulator and persistent volume
