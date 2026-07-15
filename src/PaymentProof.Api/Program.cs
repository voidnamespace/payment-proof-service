using Microsoft.EntityFrameworkCore;
using PaymentProof.Api.Endpoints;
using PaymentProof.Api.Persistence;
using PaymentProof.Api.Processing;
using PaymentProof.Api.Provider;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContextFactory<PaymentDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Database")
        ?? "Data Source=payment-proof.db";

    options.UseSqlite(connectionString);
});

builder.Services.AddHttpClient<IProviderClient, ProviderClient>((services, client) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var providerUrl = Environment.GetEnvironmentVariable("PROVIDER_URL")
        ?? configuration["Provider:Url"]
        ?? "http://localhost:8081";

    client.BaseAddress = new Uri(providerUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddSingleton<PaymentDispatchProcessor>();
builder.Services.AddHostedService<PaymentDispatchWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PaymentDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
}

app.MapHealthEndpoints();
app.MapOperationEndpoints();

await app.RunAsync();

public partial class Program;
