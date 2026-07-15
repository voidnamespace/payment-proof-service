using Microsoft.EntityFrameworkCore;
using PaymentProof.Api.Domain;

namespace PaymentProof.Api.Persistence;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options)
    : DbContext(options)
{
    public DbSet<PaymentOperation> Operations => Set<PaymentOperation>();
    public DbSet<DispatchIntent> DispatchIntents => Set<DispatchIntent>();
    public DbSet<OperationEvent> OperationEvents => Set<OperationEvent>();
    public DbSet<ProcessedReceipt> ProcessedReceipts => Set<ProcessedReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentOperation>(operation =>
        {
            operation.HasKey(x => x.OperationId);
            operation.Property(x => x.OperationId).HasMaxLength(100);
            operation.Property(x => x.Amount).HasMaxLength(32);
            operation.Property(x => x.Currency).HasMaxLength(3);
            operation.Property(x => x.Description).HasMaxLength(500);
            operation.Property(x => x.ProviderPaymentId).HasMaxLength(100);
            operation.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            operation.HasIndex(x => x.ProviderPaymentId)
                .IsUnique()
                .HasFilter("\"ProviderPaymentId\" IS NOT NULL");
        });

        modelBuilder.Entity<DispatchIntent>(intent =>
        {
            intent.HasKey(x => x.Id);
            intent.HasIndex(x => x.OperationId).IsUnique();
            intent.HasIndex(x => new { x.IsCompleted, x.NextAttemptAtUtc });
            intent.Property(x => x.LastError).HasMaxLength(1000);
            intent.HasOne(x => x.Operation)
                .WithOne(x => x.DispatchIntent)
                .HasForeignKey<DispatchIntent>(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OperationEvent>(operationEvent =>
        {
            operationEvent.HasKey(x => x.EventId);
            operationEvent.HasIndex(x => new { x.OperationId, x.EventId });
            operationEvent.Property(x => x.Type).HasMaxLength(50);
            operationEvent.Property(x => x.Message).HasMaxLength(1000);
            operationEvent.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
            operationEvent.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
            operationEvent.HasOne(x => x.Operation)
                .WithMany(x => x.Events)
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProcessedReceipt>(receipt =>
        {
            receipt.HasKey(x => x.Id);
            receipt.HasIndex(x => new { x.OperationId, x.ProviderPaymentId, x.Result }).IsUnique();
            receipt.Property(x => x.OperationId).HasMaxLength(100);
            receipt.Property(x => x.ProviderPaymentId).HasMaxLength(100);
            receipt.Property(x => x.Result).HasMaxLength(20);
            receipt.Property(x => x.Message).HasMaxLength(1000);
            receipt.HasOne<PaymentOperation>()
                .WithMany()
                .HasForeignKey(x => x.OperationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
