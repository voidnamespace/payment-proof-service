using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using PaymentProof.Api.Contracts;
using PaymentProof.Api.Domain;
using PaymentProof.Api.Persistence;

namespace PaymentProof.Api.Endpoints;

public static class OperationEndpoints
{
    private static readonly Regex AmountPattern = new(
        @"^\d+(?:\.\d{1,2})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/operations", CreateOperationAsync);
        endpoints.MapPost("/operations/{id}/submit", SubmitOperationAsync);
        endpoints.MapPost("/receipts", ProcessReceiptAsync);
        endpoints.MapGet("/operations/{id}", GetOperationAsync);
        endpoints.MapGet("/operations/{id}/events", GetOperationEventsAsync);

        return endpoints;
    }

    private static async Task<IResult> CreateOperationAsync(
        CreateOperationRequest request,
        IDbContextFactory<PaymentDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var now = DateTimeOffset.UtcNow;
        var operation = new PaymentOperation
        {
            OperationId = request.OperationId,
            Amount = request.Amount,
            Currency = "RUB",
            Description = request.Description,
            Status = OperationStatus.Created,
            CreatedAt = now,
            UpdatedAt = now
        };

        operation.Events.Add(new OperationEvent
        {
            OperationId = operation.OperationId,
            Type = "CREATED",
            FromStatus = null,
            ToStatus = OperationStatus.Created,
            Message = "Operation created",
            OccurredAt = now
        });

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.Operations.Add(operation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException
            {
                SqliteExtendedErrorCode: 1555 or 2067
            })
        {
            return Results.Conflict(new { message = "Operation already exists" });
        }

        return Results.Created(
            $"/operations/{Uri.EscapeDataString(operation.OperationId)}",
            OperationResponse.From(operation));
    }

    private static async Task<IResult> SubmitOperationAsync(
        string id,
        IDbContextFactory<PaymentDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var affectedRows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE Operations
             SET Status = {OperationStatus.Processing.ToString()}, UpdatedAt = {now}
             WHERE OperationId = {id} AND Status = {OperationStatus.Created.ToString()}
             """,
            cancellationToken);

        if (affectedRows == 1)
        {
            db.DispatchIntents.Add(new DispatchIntent
            {
                OperationId = id,
                Attempts = 0,
                NextAttemptAtUtc = now.UtcDateTime,
                CreatedAt = now,
                UpdatedAt = now,
                IsCompleted = false
            });

            db.OperationEvents.Add(new OperationEvent
            {
                OperationId = id,
                Type = "PROCESSING",
                FromStatus = OperationStatus.Created,
                ToStatus = OperationStatus.Processing,
                Message = "Payment dispatch scheduled",
                OccurredAt = now
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var accepted = await LoadOperationAsync(dbContextFactory, id, cancellationToken);
            return Results.Accepted($"/operations/{Uri.EscapeDataString(id)}", accepted);
        }

        await transaction.CommitAsync(cancellationToken);

        var current = await LoadOperationAsync(dbContextFactory, id, cancellationToken);
        return current is null ? Results.NotFound() : Results.Ok(current);
    }

    private static async Task<IResult> GetOperationAsync(
        string id,
        IDbContextFactory<PaymentDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(dbContextFactory, id, cancellationToken);
        return operation is null ? Results.NotFound() : Results.Ok(operation);
    }

    private static async Task<IResult> ProcessReceiptAsync(
        ReceiptRequest request,
        IDbContextFactory<PaymentDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var targetStatus = request.Result == "COMPLETED"
            ? OperationStatus.Completed
            : OperationStatus.Rejected;
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);

        var sqliteConnection = (SqliteConnection)db.Database.GetDbConnection();
        await using var sqliteTransaction = sqliteConnection.BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken);

        var operation = await db.Operations
            .SingleOrDefaultAsync(x => x.OperationId == request.OperationId, cancellationToken);

        if (operation is null)
        {
            await sqliteTransaction.RollbackAsync(cancellationToken);
            return Results.NotFound();
        }

        if (operation.Status == OperationStatus.Created)
        {
            await sqliteTransaction.RollbackAsync(cancellationToken);
            return Results.Conflict(new { message = "Operation has not been submitted" });
        }

        if (operation.ProviderPaymentId is not null
            && !string.Equals(
                operation.ProviderPaymentId,
                request.ProviderPaymentId,
                StringComparison.Ordinal))
        {
            await sqliteTransaction.RollbackAsync(cancellationToken);
            return Results.Conflict(new { message = "providerPaymentId does not match the operation" });
        }

        var wasAlreadyRecorded = await db.ProcessedReceipts.AnyAsync(
            x => x.OperationId == request.OperationId
                && x.ProviderPaymentId == request.ProviderPaymentId
                && x.Result == request.Result,
            cancellationToken);

        if (wasAlreadyRecorded)
        {
            await sqliteTransaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        }

        operation.ProviderPaymentId ??= request.ProviderPaymentId;

        var isFinal = operation.Status is OperationStatus.Completed or OperationStatus.Rejected;
        var isOppositeLateReceipt = isFinal && operation.Status != targetStatus;

        db.ProcessedReceipts.Add(new ProcessedReceipt
        {
            OperationId = request.OperationId,
            ProviderPaymentId = request.ProviderPaymentId,
            Result = request.Result,
            Message = request.Message,
            ProviderOccurredAt = request.OccurredAt,
            WasIgnored = isOppositeLateReceipt,
            ReceivedAt = now
        });

        if (!isFinal)
        {
            operation.Status = targetStatus;
            operation.UpdatedAt = now;

            db.OperationEvents.Add(new OperationEvent
            {
                OperationId = operation.OperationId,
                Type = request.Result,
                FromStatus = OperationStatus.Processing,
                ToStatus = targetStatus,
                Message = string.IsNullOrWhiteSpace(request.Message)
                    ? $"Payment {request.Result.ToLowerInvariant()}"
                    : request.Message,
                OccurredAt = now
            });
        }

        var dispatchIntent = await db.DispatchIntents
            .SingleOrDefaultAsync(x => x.OperationId == request.OperationId, cancellationToken);

        if (dispatchIntent is not null)
        {
            dispatchIntent.IsCompleted = true;
            dispatchIntent.LastError = null;
            dispatchIntent.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await sqliteTransaction.CommitAsync(cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> GetOperationEventsAsync(
        string id,
        IDbContextFactory<PaymentDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Operations.AsNoTracking()
            .AnyAsync(x => x.OperationId == id, cancellationToken);

        if (!exists)
        {
            return Results.NotFound();
        }

        var events = await db.OperationEvents.AsNoTracking()
            .Where(x => x.OperationId == id)
            .OrderBy(x => x.EventId)
            .Select(x => OperationEventResponse.From(x))
            .ToListAsync(cancellationToken);

        return Results.Ok(events);
    }

    private static async Task<OperationResponse?> LoadOperationAsync(
        IDbContextFactory<PaymentDbContext> dbContextFactory,
        string id,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var operation = await db.Operations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == id, cancellationToken);

        return operation is null ? null : OperationResponse.From(operation);
    }

    private static Dictionary<string, string[]> Validate(CreateOperationRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 100)
        {
            errors["operationId"] = ["operationId is required and must not exceed 100 characters"];
        }

        var isValidAmount = !string.IsNullOrWhiteSpace(request.Amount)
            && AmountPattern.IsMatch(request.Amount)
            && decimal.TryParse(
                request.Amount,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount)
            && amount > 0;

        if (!isValidAmount)
        {
            errors["amount"] = ["amount must be a positive decimal string with at most two decimal places"];
        }

        if (!string.Equals(request.Currency, "RUB", StringComparison.Ordinal))
        {
            errors["currency"] = ["only RUB is supported"];
        }

        if (request.Description?.Length > 500)
        {
            errors["description"] = ["description must not exceed 500 characters"];
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(ReceiptRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 100)
        {
            errors["operationId"] = ["operationId is required and must not exceed 100 characters"];
        }

        if (string.IsNullOrWhiteSpace(request.ProviderPaymentId)
            || request.ProviderPaymentId.Length > 100)
        {
            errors["providerPaymentId"] =
                ["providerPaymentId is required and must not exceed 100 characters"];
        }

        if (request.Result is not ("COMPLETED" or "REJECTED"))
        {
            errors["result"] = ["result must be COMPLETED or REJECTED"];
        }

        if (request.Message?.Length > 1000)
        {
            errors["message"] = ["message must not exceed 1000 characters"];
        }

        if (request.OccurredAt == default)
        {
            errors["occurredAt"] = ["occurredAt is required"];
        }

        return errors;
    }
}
