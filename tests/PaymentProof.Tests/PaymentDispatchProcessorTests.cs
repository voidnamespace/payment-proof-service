using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentProof.Api.Domain;
using PaymentProof.Api.Persistence;
using PaymentProof.Api.Processing;
using PaymentProof.Api.Provider;

namespace PaymentProof.Tests;

public sealed class PaymentDispatchProcessorTests
{
    [Fact]
    public async Task RetryAfterLostResponseUsesSamePaymentData()
    {
        await using var database = await ProcessorDatabase.CreateAsync();
        await database.SeedProcessingOperationAsync("lost-response");
        var provider = new QueueProviderClient(
            ProviderCallResult.Retryable("connection closed before response"),
            ProviderCallResult.Accepted("provider-lost-response"));
        var processor = CreateProcessor(database.Factory, provider);

        Assert.True(await processor.ProcessOneDueAsync(CancellationToken.None));

        await database.MakeIntentDueAsync("lost-response");
        Assert.True(await processor.ProcessOneDueAsync(CancellationToken.None));

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(provider.Requests[0], provider.Requests[1]);

        await using var db = await database.Factory.CreateDbContextAsync();
        var operation = await db.Operations.SingleAsync(x => x.OperationId == "lost-response");
        var intent = await db.DispatchIntents.SingleAsync(x => x.OperationId == "lost-response");
        Assert.Equal(OperationStatus.Processing, operation.Status);
        Assert.Equal("provider-lost-response", operation.ProviderPaymentId);
        Assert.True(intent.IsCompleted);
        Assert.Equal(2, intent.Attempts);
    }

    [Fact]
    public async Task CallbackBeforeHttpResponseCannotBeRevertedByLateAcceptedResponse()
    {
        await using var database = await ProcessorDatabase.CreateAsync();
        await database.SeedProcessingOperationAsync("early-callback");
        var provider = new CallbackBeforeResponseProvider(database.Factory);
        var processor = CreateProcessor(database.Factory, provider);

        Assert.True(await processor.ProcessOneDueAsync(CancellationToken.None));

        await using var db = await database.Factory.CreateDbContextAsync();
        var operation = await db.Operations.SingleAsync(x => x.OperationId == "early-callback");
        var intent = await db.DispatchIntents.SingleAsync(x => x.OperationId == "early-callback");
        Assert.Equal(OperationStatus.Completed, operation.Status);
        Assert.Equal("provider-early", operation.ProviderPaymentId);
        Assert.True(intent.IsCompleted);
    }

    [Fact]
    public async Task NewProcessorContinuesPersistedIntent()
    {
        await using var database = await ProcessorDatabase.CreateAsync();
        await database.SeedProcessingOperationAsync("restart-operation");

        var unavailableProvider = new QueueProviderClient(
            ProviderCallResult.Retryable("provider unavailable"));
        var firstProcess = CreateProcessor(database.Factory, unavailableProvider);
        await firstProcess.ProcessOneDueAsync(CancellationToken.None);

        await database.MakeIntentDueAsync("restart-operation");

        var recoveredProvider = new QueueProviderClient(
            ProviderCallResult.Accepted("provider-after-restart"));
        var restartedProcess = CreateProcessor(database.Factory, recoveredProvider);
        await restartedProcess.ProcessOneDueAsync(CancellationToken.None);

        await using var db = await database.Factory.CreateDbContextAsync();
        var operation = await db.Operations.SingleAsync(x => x.OperationId == "restart-operation");
        Assert.Equal("provider-after-restart", operation.ProviderPaymentId);
        Assert.True((await db.DispatchIntents.SingleAsync(
            x => x.OperationId == "restart-operation")).IsCompleted);
    }

    private static PaymentDispatchProcessor CreateProcessor(
        IDbContextFactory<PaymentDbContext> factory,
        IProviderClient provider) =>
        new(factory, provider, NullLogger<PaymentDispatchProcessor>.Instance);

    private sealed class QueueProviderClient(params ProviderCallResult[] results)
        : IProviderClient
    {
        private readonly Queue<ProviderCallResult> _results = new(results);
        public List<ProviderPaymentRequest> Requests { get; } = [];

        public Task<ProviderCallResult> CreatePaymentAsync(
            ProviderPaymentRequest payment,
            CancellationToken cancellationToken)
        {
            Requests.Add(payment);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CallbackBeforeResponseProvider(
        IDbContextFactory<PaymentDbContext> factory) : IProviderClient
    {
        public async Task<ProviderCallResult> CreatePaymentAsync(
            ProviderPaymentRequest payment,
            CancellationToken cancellationToken)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var operation = await db.Operations.SingleAsync(
                x => x.OperationId == payment.OperationId,
                cancellationToken);
            var intent = await db.DispatchIntents.SingleAsync(
                x => x.OperationId == payment.OperationId,
                cancellationToken);

            operation.Status = OperationStatus.Completed;
            operation.ProviderPaymentId = "provider-early";
            operation.UpdatedAt = DateTimeOffset.UtcNow;
            intent.IsCompleted = true;
            intent.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return ProviderCallResult.Accepted("provider-early");
        }
    }

    private sealed class ProcessorDatabase(
        string path,
        TestDbContextFactory factory) : IAsyncDisposable
    {
        public TestDbContextFactory Factory { get; } = factory;

        public static async Task<ProcessorDatabase> CreateAsync()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"payment-processor-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseSqlite($"Data Source={path};Default Timeout=30")
                .Options;
            var factory = new TestDbContextFactory(options);

            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new ProcessorDatabase(path, factory);
        }

        public async Task SeedProcessingOperationAsync(string operationId)
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = await Factory.CreateDbContextAsync();
            db.Operations.Add(new PaymentOperation
            {
                OperationId = operationId,
                Amount = "100.00",
                Currency = "RUB",
                Description = "Processor test",
                Status = OperationStatus.Processing,
                CreatedAt = now,
                UpdatedAt = now,
                DispatchIntent = new DispatchIntent
                {
                    OperationId = operationId,
                    Attempts = 0,
                    NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1),
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsCompleted = false
                }
            });
            await db.SaveChangesAsync();
        }

        public async Task MakeIntentDueAsync(string operationId)
        {
            await using var db = await Factory.CreateDbContextAsync();
            await db.DispatchIntents
                .Where(x => x.OperationId == operationId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteIfExists(path);
            DeleteIfExists($"{path}-shm");
            DeleteIfExists($"{path}-wal");
            return ValueTask.CompletedTask;
        }

        private static void DeleteIfExists(string file)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    public sealed class TestDbContextFactory(DbContextOptions<PaymentDbContext> options)
        : IDbContextFactory<PaymentDbContext>
    {
        public PaymentDbContext CreateDbContext() => new(options);

        public Task<PaymentDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
