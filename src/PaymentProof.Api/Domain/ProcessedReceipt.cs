namespace PaymentProof.Api.Domain;

public sealed class ProcessedReceipt
{
    public long Id { get; set; }
    public required string OperationId { get; set; }
    public required string ProviderPaymentId { get; set; }
    public required string Result { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset ProviderOccurredAt { get; set; }
    public bool WasIgnored { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
