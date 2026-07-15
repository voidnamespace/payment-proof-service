using System.Net;
using System.Net.Http.Json;
using PaymentProof.Api.Contracts;

namespace PaymentProof.Tests;

public sealed class ReceiptEndpointsTests(PaymentApiFactory factory)
    : IClassFixture<PaymentApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ReceiptBeforeProviderResponseFinalizesOperation()
    {
        var operationId = await CreateAndSubmitAsync();

        var response = await SendReceiptAsync(operationId, "provider-1", "COMPLETED");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var operation = await GetOperationAsync(operationId);
        Assert.Equal("COMPLETED", operation.Status);
        Assert.Equal("provider-1", operation.ProviderPaymentId);

        var events = await GetEventsAsync(operationId);
        Assert.Equal(["CREATED", "PROCESSING", "COMPLETED"], events.Select(x => x.Type));
    }

    [Fact]
    public async Task DuplicateReceiptDoesNotCreateAnotherTransition()
    {
        var operationId = await CreateAndSubmitAsync();

        var first = await SendReceiptAsync(operationId, "provider-2", "REJECTED");
        var duplicate = await SendReceiptAsync(operationId, "provider-2", "REJECTED");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, duplicate.StatusCode);

        var operation = await GetOperationAsync(operationId);
        Assert.Equal("REJECTED", operation.Status);
        Assert.Equal(3, (await GetEventsAsync(operationId)).Count);
    }

    [Fact]
    public async Task OppositeLateReceiptIsIgnored()
    {
        var operationId = await CreateAndSubmitAsync();
        await SendReceiptAsync(operationId, "provider-3", "COMPLETED");

        var late = await SendReceiptAsync(operationId, "provider-3", "REJECTED");

        Assert.Equal(HttpStatusCode.NoContent, late.StatusCode);
        Assert.Equal("COMPLETED", (await GetOperationAsync(operationId)).Status);
        Assert.Equal(3, (await GetEventsAsync(operationId)).Count);
    }

    [Fact]
    public async Task ReceiptWithDifferentProviderPaymentIdReturnsConflict()
    {
        var operationId = await CreateAndSubmitAsync();
        await SendReceiptAsync(operationId, "provider-original", "COMPLETED");

        var mismatch = await SendReceiptAsync(operationId, "provider-other", "REJECTED");

        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        var operation = await GetOperationAsync(operationId);
        Assert.Equal("provider-original", operation.ProviderPaymentId);
        Assert.Equal("COMPLETED", operation.Status);
    }

    [Fact]
    public async Task ConcurrentDuplicateReceiptsCreateOneFinalTransition()
    {
        var operationId = await CreateAndSubmitAsync();

        var calls = Enumerable.Range(0, 20)
            .Select(_ => SendReceiptAsync(operationId, "provider-concurrent", "COMPLETED"));
        var responses = await Task.WhenAll(calls);

        Assert.All(responses, x => Assert.Equal(HttpStatusCode.NoContent, x.StatusCode));
        Assert.Equal("COMPLETED", (await GetOperationAsync(operationId)).Status);
        Assert.Equal(3, (await GetEventsAsync(operationId)).Count);
    }

    [Fact]
    public async Task ReceiptForCreatedOperationReturnsConflict()
    {
        var operationId = NewOperationId();
        await CreateOperationAsync(operationId);

        var response = await SendReceiptAsync(operationId, "provider-too-early", "COMPLETED");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CREATED", (await GetOperationAsync(operationId)).Status);
    }

    [Fact]
    public async Task ReceiptWithInvalidResultReturnsBadRequest()
    {
        var operationId = await CreateAndSubmitAsync();

        var response = await SendReceiptAsync(operationId, "provider-invalid", "UNKNOWN");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PROCESSING", (await GetOperationAsync(operationId)).Status);
    }

    private async Task<string> CreateAndSubmitAsync()
    {
        var operationId = NewOperationId();
        await CreateOperationAsync(operationId);
        var submit = await _client.PostAsync($"/operations/{operationId}/submit", content: null);
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        return operationId;
    }

    private async Task CreateOperationAsync(string operationId)
    {
        var response = await _client.PostAsJsonAsync("/operations", new CreateOperationRequest(
            operationId,
            "250.00",
            "RUB",
            "Receipt test"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private Task<HttpResponseMessage> SendReceiptAsync(
        string operationId,
        string providerPaymentId,
        string result) =>
        _client.PostAsJsonAsync("/receipts", new ReceiptRequest(
            providerPaymentId,
            operationId,
            result,
            $"Provider says {result}",
            DateTimeOffset.UtcNow));

    private async Task<OperationResponse> GetOperationAsync(string operationId) =>
        (await _client.GetFromJsonAsync<OperationResponse>($"/operations/{operationId}"))!;

    private async Task<List<OperationEventResponse>> GetEventsAsync(string operationId) =>
        (await _client.GetFromJsonAsync<List<OperationEventResponse>>(
            $"/operations/{operationId}/events"))!;

    private static string NewOperationId() => $"receipt-{Guid.NewGuid():N}";
}
