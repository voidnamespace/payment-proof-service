namespace PaymentProof.Api.Contracts;

public sealed record ReceiptRequest(
    string ProviderPaymentId,
    string OperationId,
    string Result,
    string? Message,
    DateTimeOffset OccurredAt);
