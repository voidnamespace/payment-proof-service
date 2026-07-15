using PaymentProof.Api.Domain;

namespace PaymentProof.Api.Contracts;

public sealed record OperationResponse(
    string OperationId,
    string Amount,
    string Currency,
    string? Description,
    string Status,
    string? ProviderPaymentId)
{
    public static OperationResponse From(PaymentOperation operation) => new(
        operation.OperationId,
        operation.Amount,
        operation.Currency,
        operation.Description,
        operation.Status.ToString().ToUpperInvariant(),
        operation.ProviderPaymentId);
}
