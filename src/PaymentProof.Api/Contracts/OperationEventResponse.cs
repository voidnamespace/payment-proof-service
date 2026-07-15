using PaymentProof.Api.Domain;

namespace PaymentProof.Api.Contracts;

public sealed record OperationEventResponse(
    long EventId,
    string Type,
    string? FromStatus,
    string ToStatus,
    string Message,
    DateTimeOffset OccurredAt)
{
    public static OperationEventResponse From(OperationEvent operationEvent) => new(
        operationEvent.EventId,
        operationEvent.Type,
        operationEvent.FromStatus?.ToString().ToUpperInvariant(),
        operationEvent.ToStatus.ToString().ToUpperInvariant(),
        operationEvent.Message,
        operationEvent.OccurredAt);
}
