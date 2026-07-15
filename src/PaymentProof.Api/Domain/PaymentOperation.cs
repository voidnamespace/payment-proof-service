namespace PaymentProof.Api.Domain;

public sealed class PaymentOperation
{
    public required string OperationId { get; set; }
    public required string Amount { get; set; }
    public string Currency { get; set; } = "RUB";
    public string? Description { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Created;
    public string? ProviderPaymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public DispatchIntent? DispatchIntent { get; set; }
    public List<OperationEvent> Events { get; set; } = [];
}
