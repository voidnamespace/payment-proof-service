namespace PaymentProof.Api.Domain;

public sealed class OperationEvent
{
    public long EventId { get; set; }
    public required string OperationId { get; set; }
    public required string Type { get; set; }
    public OperationStatus? FromStatus { get; set; }
    public required OperationStatus ToStatus { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public PaymentOperation Operation { get; set; } = null!;
}
