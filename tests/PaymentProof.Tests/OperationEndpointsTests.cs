using System.Net;
using System.Net.Http.Json;
using PaymentProof.Api.Contracts;

namespace PaymentProof.Tests;

public sealed class OperationEndpointsTests(PaymentApiFactory factory)
    : IClassFixture<PaymentApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CreateOperation_CreatesOperationAndInitialEvent()
    {
        var id = NewOperationId();

        var response = await CreateOperationAsync(id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var operation = await response.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.NotNull(operation);
        Assert.Equal(id, operation.OperationId);
        Assert.Equal("1000.00", operation.Amount);
        Assert.Equal("RUB", operation.Currency);
        Assert.Equal("CREATED", operation.Status);
        Assert.Null(operation.ProviderPaymentId);

        var events = await _client.GetFromJsonAsync<List<OperationEventResponse>>(
            $"/operations/{id}/events");

        var created = Assert.Single(events!);
        Assert.Equal("CREATED", created.Type);
        Assert.Null(created.FromStatus);
        Assert.Equal("CREATED", created.ToStatus);
    }

    [Fact]
    public async Task CreateOperation_WithExistingId_ReturnsConflict()
    {
        var id = NewOperationId();
        await CreateOperationAsync(id);

        var duplicate = await CreateOperationAsync(id);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.234")]
    [InlineData("1,20")]
    [InlineData("")]
    public async Task CreateOperation_WithInvalidAmount_ReturnsBadRequest(string amount)
    {
        var response = await _client.PostAsJsonAsync("/operations", new CreateOperationRequest(
            NewOperationId(),
            amount,
            "RUB",
            "Invalid amount"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitOperation_RepeatedRequestDoesNotCreateSecondTransition()
    {
        var id = NewOperationId();
        await CreateOperationAsync(id);

        var first = await _client.PostAsync($"/operations/{id}/submit", content: null);
        var second = await _client.PostAsync($"/operations/{id}/submit", content: null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var operation = await second.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal("PROCESSING", operation!.Status);

        var events = await _client.GetFromJsonAsync<List<OperationEventResponse>>(
            $"/operations/{id}/events");

        Assert.Equal(2, events!.Count);
        Assert.Equal(["CREATED", "PROCESSING"], events.Select(x => x.Type));
    }

    [Fact]
    public async Task SubmitOperation_ConcurrentRequestsCreateOneIntent()
    {
        var id = NewOperationId();
        await CreateOperationAsync(id);

        var requests = Enumerable.Range(0, 20)
            .Select(_ => _client.PostAsync($"/operations/{id}/submit", content: null));

        var responses = await Task.WhenAll(requests);

        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(19, responses.Count(x => x.StatusCode == HttpStatusCode.OK));

        var events = await _client.GetFromJsonAsync<List<OperationEventResponse>>(
            $"/operations/{id}/events");

        Assert.Equal(2, events!.Count);
    }

    private Task<HttpResponseMessage> CreateOperationAsync(string operationId) =>
        _client.PostAsJsonAsync("/operations", new CreateOperationRequest(
            operationId,
            "1000.00",
            "RUB",
            "Test payment"));

    private static string NewOperationId() => $"operation-{Guid.NewGuid():N}";
}
