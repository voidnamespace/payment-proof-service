using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PaymentProof.Api.Domain;
using PaymentProof.Api.Persistence;
using PaymentProof.Api.Provider;

namespace PaymentProof.Api.Processing;

public sealed class PaymentDispatchProcessor(
    IDbContextFactory<PaymentDbContext> dbContextFactory,
    IProviderClient providerClient,
    ILogger<PaymentDispatchProcessor> logger)
{
    public async Task<bool> ProcessOneDueAsync(CancellationToken cancellationToken)
    {
        DispatchSnapshot? dispatch;

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var now = DateTime.UtcNow;
            dispatch = await db.DispatchIntents.AsNoTracking()
                .Where(x => !x.IsCompleted && x.NextAttemptAtUtc <= now)
                .OrderBy(x => x.NextAttemptAtUtc)
                .ThenBy(x => x.Id)
                .Select(x => new DispatchSnapshot(
                    x.Id,
                    x.OperationId,
                    x.Operation.Amount,
                    x.Operation.Currency,
                    x.Operation.Status,
                    x.Operation.ProviderPaymentId,
                    x.Attempts))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (dispatch is null)
        {
            return false;
        }

        if (dispatch.Status is OperationStatus.Completed or OperationStatus.Rejected
            && dispatch.ProviderPaymentId is not null)
        {
            await CompleteIntentAsync(dispatch.IntentId, null, cancellationToken);
            return true;
        }

        var attempt = dispatch.Attempts + 1;
        var nextAttemptAtUtc = DateTime.UtcNow + CalculateBackoff(attempt);

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var affected = await db.DispatchIntents
                .Where(x => x.Id == dispatch.IntentId && !x.IsCompleted)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Attempts, attempt)
                        .SetProperty(x => x.NextAttemptAtUtc, nextAttemptAtUtc)
                        .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                    cancellationToken);

            if (affected == 0)
            {
                return true;
            }
        }

        logger.LogInformation(
            "Dispatching payment {OperationId}, attempt {Attempt}",
            dispatch.OperationId,
            attempt);

        var result = await providerClient.CreatePaymentAsync(
            new ProviderPaymentRequest(
                dispatch.OperationId,
                dispatch.Amount,
                dispatch.Currency),
            cancellationToken);

        switch (result.Outcome)
        {
            case ProviderCallOutcome.Accepted:
                await SaveAcceptedResponseAsync(
                    dispatch.IntentId,
                    dispatch.OperationId,
                    result.ProviderPaymentId!,
                    attempt,
                    cancellationToken);
                break;

            case ProviderCallOutcome.RetryableFailure:
                await SaveRetryableFailureAsync(
                    dispatch.IntentId,
                    dispatch.OperationId,
                    attempt,
                    result.Error!,
                    cancellationToken);
                break;

            case ProviderCallOutcome.PermanentFailure:
                await CompleteIntentAsync(dispatch.IntentId, result.Error, cancellationToken);
                logger.LogError(
                    "Provider permanently rejected dispatch {OperationId}: {Error}",
                    dispatch.OperationId,
                    result.Error);
                break;

            default:
                throw new InvalidOperationException($"Unknown provider outcome: {result.Outcome}");
        }

        return true;
    }

    private async Task SaveAcceptedResponseAsync(
        long intentId,
        string operationId,
        string providerPaymentId,
        int attempt,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);

        var sqliteConnection = (SqliteConnection)db.Database.GetDbConnection();
        await using var transaction = sqliteConnection.BeginTransaction(deferred: false);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE Operations
             SET ProviderPaymentId = CASE
                     WHEN ProviderPaymentId IS NULL THEN {providerPaymentId}
                     ELSE ProviderPaymentId
                 END,
                 UpdatedAt = CASE
                     WHEN ProviderPaymentId IS NULL THEN {now}
                     ELSE UpdatedAt
                 END
             WHERE OperationId = {operationId}
               AND (ProviderPaymentId IS NULL OR ProviderPaymentId = {providerPaymentId})
             """,
            cancellationToken);

        if (affected == 0)
        {
            var existingProviderPaymentId = await db.Operations.AsNoTracking()
                .Where(x => x.OperationId == operationId)
                .Select(x => x.ProviderPaymentId)
                .SingleOrDefaultAsync(cancellationToken);

            var error = existingProviderPaymentId is null
                ? "Operation disappeared while provider response was being saved"
                : $"Provider ID mismatch: stored {existingProviderPaymentId}, received {providerPaymentId}";

            await db.DispatchIntents
                .Where(x => x.Id == intentId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.IsCompleted, true)
                        .SetProperty(x => x.LastError, error)
                        .SetProperty(x => x.UpdatedAt, now),
                    cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            logger.LogCritical(
                "Cannot associate provider response for {OperationId}: {Error}",
                operationId,
                error);
            return;
        }

        await db.DispatchIntents
            .Where(x => x.Id == intentId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.IsCompleted, true)
                    .SetProperty(x => x.LastError, (string?)null)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Provider accepted payment {OperationId} as {ProviderPaymentId} on attempt {Attempt}",
            operationId,
            providerPaymentId,
            attempt);
    }

    private async Task SaveRetryableFailureAsync(
        long intentId,
        string operationId,
        int attempt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.DispatchIntents
            .Where(x => x.Id == intentId && !x.IsCompleted)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);

        logger.LogWarning(
            "Payment dispatch {OperationId}, attempt {Attempt}, will be retried: {Error}",
            operationId,
            attempt,
            error);
    }

    private async Task CompleteIntentAsync(
        long intentId,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.DispatchIntents
            .Where(x => x.Id == intentId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.IsCompleted, true)
                    .SetProperty(x => x.LastError, error)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    private static TimeSpan CalculateBackoff(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 7);
        var baseMilliseconds = Math.Min(250 * (1 << exponent), 30_000);
        var jitterMilliseconds = Random.Shared.Next(0, 251);
        return TimeSpan.FromMilliseconds(baseMilliseconds + jitterMilliseconds);
    }

    private sealed record DispatchSnapshot(
        long IntentId,
        string OperationId,
        string Amount,
        string Currency,
        OperationStatus Status,
        string? ProviderPaymentId,
        int Attempts);
}
