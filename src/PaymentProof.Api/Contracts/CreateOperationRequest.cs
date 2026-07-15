namespace PaymentProof.Api.Contracts;

public sealed record CreateOperationRequest(
    string OperationId,
    string Amount,
    string Currency,
    string? Description);
