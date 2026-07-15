namespace PaymentProof.Api.Domain;

public sealed class DispatchIntent
{
    public long Id { get; set; }
    public required string OperationId { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastError { get; set; }
    public bool IsCompleted { get; set; }

    public PaymentOperation Operation { get; set; } = null!;
}
