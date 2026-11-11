using AutoReparos.Cobranca.Api;
using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;
using AutoReparos.Cobranca.Infrastructure;
using Amazon;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace AutoReparos.Cobranca.Tests;

public sealed partial class BillingLifecycleTests
{
    private sealed class Store : IBillingStore
    {
        public readonly List<Orcamento> Items = [];
        public Task<Orcamento?> ObterOrcamentoAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task<Pagamento?> ObterPagamentoAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.Select(x => x.Pagamento).SingleOrDefault(x => x?.Id == id));
        public Task<Orcamento?> ObterPorOrdemAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.OrdemServicoId == id));
        public Task<Orcamento?> ObterPorPagamentoAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Pagamento?.Id == id));
        public Task AdicionarOrcamentoAsync(Orcamento orcamento, CancellationToken ct) { Items.Add(orcamento); return Task.CompletedTask; }
        public readonly List<MensagemSaida> Mensagens = [];
        public void AdicionarMensagemSaida(MensagemSaida mensagem) => Mensagens.Add(mensagem);
        public Task SalvarAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Provider : ICheckoutProvider
    {
        public int Checkouts { get; private set; }
        public int RefundCalls { get; private set; }
        public (Guid PagamentoId, StatusPagamento Status, string Referencia)? WebhookResult { get; set; }
        public List<string> CheckoutKeys { get; } = [];
        public Task<AbrirCheckout> CriarCheckoutAsync(Guid pagamentoId, decimal valor, string idempotencyKey, CancellationToken ct)
        {
            // Requisicoes concorrentes chamam o provedor ao mesmo tempo: o registro das chaves precisa ser atomico.
            lock (CheckoutKeys)
            {
                Checkouts++; CheckoutKeys.Add(idempotencyKey);
                // Provedor falso: a mesma chave devolve a mesma preferencia; chave nova gera URL nova.
                return Task.FromResult(new AbrirCheckout($"https://checkout.example/{CheckoutKeys.Distinct().ToList().IndexOf(idempotencyKey) + 1}", pagamentoId.ToString()));
            }
        }
        public Task<string> ReembolsarAsync(string referencia, decimal valor, string idempotencyKey, CancellationToken ct) { RefundCalls++; return Task.FromResult("refund-123"); }
        public Task<(Guid PagamentoId, StatusPagamento Status, string Referencia)?> ConsultarPagamentoAsync(string id, CancellationToken ct) => Task.FromResult(WebhookResult);
    }
    private sealed class Publisher(int failAttempts = 0) : IResultadoPublisher
    {
        public readonly List<EventoPagamento> Events = [];
        public int Attempts { get; private set; }
        public Task PublicarAsync(EventoPagamento evento, CancellationToken ct) { if (Attempts++ < failAttempts) throw new InvalidOperationException("SQS indisponível"); Events.Add(evento); return Task.CompletedTask; }
    }
    private sealed class Alerts : IAlertSink { public int Count { get; private set; } public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { Count++; return Task.CompletedTask; } }
    private sealed class Readiness(bool completed) : IOrderReadiness { public Task<bool> ServicoConcluidoAsync(Guid id, CancellationToken ct) => Task.FromResult(completed); }
    private static (BillingService Service, Store Store, Provider Provider, Publisher Publisher) Setup(bool completed = true)
    {
        var store = new Store(); var provider = new Provider(); var publisher = new Publisher();
        return (new BillingService(store, provider, publisher, new Alerts(), new Readiness(completed)), store, provider, publisher);
    }
    private static async Task<Pagamento> EligiblePayment(BillingService service)
    {
        var budget = await service.CriarOrcamentoAsync(new(Guid.NewGuid(), Guid.NewGuid(), 250m), CancellationToken.None);
        budget.Aprovar(); return budget.Pagamento!;
    }
    private static HttpClient Authorized(HttpClient client, string role = "Administrador", Guid? clienteId = null)
    { client.DefaultRequestHeaders.Add("X-Test-Role", role); if (clienteId is not null) client.DefaultRequestHeaders.Add("X-Test-Cliente", clienteId.ToString()!); return client; }

    [Fact]
    public async Task CreatesBudgetForOrder()
    {
        var store = new Store(); var provider = new Provider(); var publisher = new Publisher(); var alerts = new Alerts();
        using var factory = new BillingApiFactory(store, provider, publisher, alerts, new Readiness(true));
        using var client = factory.CreateClient(); var request = new { ordemServicoId = Guid.NewGuid(), clienteId = Guid.NewGuid(), valor = 250m };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/orcamentos", request)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "Cliente");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/orcamentos", request)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", "ServicoOS");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/v1/orcamentos", new { request.ordemServicoId, request.clienteId, valor = 0m })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/v1/orcamentos", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.Single(store.Items); Assert.Equal(StatusOrcamento.Pendente, store.Items[0].Status);
        var created = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); var budgetId = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/orcamentos/{budgetId}/aprovacao", created.GetProperty("approvalUrl").GetString());
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/v1/orcamentos/{budgetId}/aprovacao", null)).StatusCode);
        Authorized(client, "Cliente", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/orcamentos/{budgetId}/aprovacao", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Cliente"); client.DefaultRequestHeaders.Add("X-Test-Cliente", request.clienteId.ToString());
        var approved = await client.PostAsync($"/api/v1/orcamentos/{budgetId}/aprovacao", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode); Assert.Equal(StatusOrcamento.Aprovado, store.Items[0].Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/orcamentos/{budgetId}/aprovacao", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/orcamentos/{Guid.NewGuid()}/aprovacao", null)).StatusCode);
    }

    [Fact]
    public async Task ChargeRequiresApprovalAndCompletion()
    {
        var store = new Store(); var provider = new Provider(); var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow);
        var payment = new Pagamento(budget.Id, budget.Valor); budget.DefinirPagamento(payment); store.Items.Add(budget);
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(false));
        using var client = Authorized(factory.CreateClient());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        budget.Aprovar();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(0, provider.Checkouts);
    }

    [Fact]
    public async Task CreatesSingleCheckoutAttempt()
    {
        var store = new Store(); var provider = new Provider(); var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); budget.Aprovar(); store.Items.Add(budget);
        var payment = budget.Pagamento!;
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        using var client = Authorized(factory.CreateClient());
        var first = await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null);
        var second = await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("https://checkout.example/1", (await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("urlCheckout").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/pagamentos/{Guid.NewGuid()}/checkout", null)).StatusCode);
        Assert.Equal(1, provider.Checkouts); Assert.Equal(StatusPagamento.Pendente, payment.Status);
    }

    [Fact]
    public async Task WebhookSignatureValidation()
    {
        const string secret = "webhook-test-secret"; const string timestamp = "1712345678";
        var manifest = "id:payment-1;request-id:request-1;ts:" + timestamp + ";";
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest)));
        var store = new Store(); var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); budget.Aprovar(); store.Items.Add(budget);
        var payment = budget.Pagamento!; payment.IniciarCheckout("https://checkout.example/1", "preference-1");
        var provider = new Provider { WebhookResult = (payment.Id, StatusPagamento.Capturado, "mp-payment-1") };
        var publisher = new Publisher(); using var factory = new BillingApiFactory(store, provider, publisher, new Alerts(), new Readiness(true));
        using var client = factory.CreateClient();
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mercado-pago")
        { Content = JsonContent.Create(new { data = new { id = "payment-1" } }) };
        valid.Headers.Add("x-signature", $"ts={timestamp},v1={hash}"); valid.Headers.Add("x-request-id", "request-1");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(valid)).StatusCode);
        Assert.Equal(StatusPagamento.Capturado, payment.Status); Assert.Single(publisher.Events);
        using var duplicate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mercado-pago")
        { Content = JsonContent.Create(new { data = new { id = "payment-1" } }) };
        duplicate.Headers.Add("x-signature", $"ts={timestamp},v1={hash}"); duplicate.Headers.Add("x-request-id", "request-1");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(duplicate)).StatusCode); Assert.Single(publisher.Events);
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mercado-pago")
        { Content = JsonContent.Create(new { data = new { id = "payment-1" } }) };
        invalid.Headers.Add("x-signature", "ts=broken,v1=xyz"); invalid.Headers.Add("x-request-id", "request-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(invalid)).StatusCode);
        using var wrongDigest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mercado-pago") { Content = JsonContent.Create(new { data = new { id = "payment-1" } }) };
        wrongDigest.Headers.Add("x-signature", $"ts={timestamp},v1={new string('0', 64)}"); wrongDigest.Headers.Add("x-request-id", "request-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongDigest)).StatusCode);
        Assert.Equal(StatusPagamento.Capturado, payment.Status); Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task ManualRetryAfterTerminalState()
    {
        var store = new Store(); var provider = new Provider(); var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); budget.Aprovar(); store.Items.Add(budget);
        var payment = budget.Pagamento!;
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        using var client = Authorized(factory.CreateClient());
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "Cliente");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", "Administrador");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(1, provider.Checkouts);
        payment.AplicarEstado(StatusPagamento.Rejeitado, "provider-1");
        client.DefaultRequestHeaders.Remove("X-Test-Role");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "Cliente");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", "Atendente");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(2, provider.Checkouts);
    }

    [Fact]
    public async Task CapturedPaymentForwardRecovery()
    {
        var store = new Store(); var provider = new Provider(); var publisher = new Publisher(failAttempts: 1); var alerts = new Alerts();
        var service = new BillingService(store, provider, publisher, alerts, new Readiness(true));
        var budget = await service.CriarOrcamentoAsync(new(Guid.NewGuid(), Guid.NewGuid(), 250m), CancellationToken.None); budget.Aprovar(); var payment = budget.Pagamento!;
        await service.AbrirCheckoutAsync(payment.Id, null, CancellationToken.None);
        await Assert.ThrowsAsync<PublicacaoResultadoException>(() => service.AplicarWebhookAsync(payment.Id, StatusPagamento.Capturado, "provider-1", CancellationToken.None));
        await service.AplicarWebhookAsync(payment.Id, StatusPagamento.Capturado, "provider-1", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AbrirCheckoutAsync(payment.Id, null, CancellationToken.None));
        Assert.Equal(StatusPagamento.Capturado, payment.Status); Assert.True(payment.ResultadoPublicado); Assert.Single(publisher.Events);
        Assert.Equal(1, provider.Checkouts); Assert.Equal(1, alerts.Count); Assert.Empty(payment.Reembolsos);
    }

    [Theory]
    [InlineData(StatusPagamento.Rejeitado)]
    [InlineData(StatusPagamento.Cancelado)]
    [InlineData(StatusPagamento.Expirado)]
    public async Task ManualRetryAfterTerminalStateUsesNewIdempotencyKeyAndNewUrl(StatusPagamento terminal)
    {
        var (service, _, provider, _) = Setup();
        var payment = await EligiblePayment(service);
        var first = await service.AbrirCheckoutAsync(payment.Id, null, CancellationToken.None);
        var firstUrl = first.UrlCheckout; var firstKey = Assert.Single(provider.CheckoutKeys);
        Assert.Equal($"checkout-{payment.Id:N}-1", firstKey); Assert.Equal(1, payment.Tentativas);
        // Chamada repetida durante a tentativa ativa nao cria outra nem consome tentativa.
        await service.AbrirCheckoutAsync(payment.Id, null, CancellationToken.None);
        Assert.Single(provider.CheckoutKeys); Assert.Equal(1, payment.Tentativas);
        await service.AplicarWebhookAsync(payment.Id, terminal, "provider-1", CancellationToken.None);
        var retry = await service.AbrirCheckoutAsync(payment.Id, null, CancellationToken.None);
        Assert.Equal(2, provider.CheckoutKeys.Count); Assert.Equal($"checkout-{payment.Id:N}-2", provider.CheckoutKeys[1]);
        Assert.NotEqual(firstKey, provider.CheckoutKeys[1]); Assert.NotEqual(firstUrl, retry.UrlCheckout);
        Assert.Equal("https://checkout.example/2", retry.UrlCheckout);
        Assert.Equal(StatusPagamento.Pendente, retry.Status); Assert.Equal(2, payment.Tentativas);
    }

    [Fact]
    public async Task ApprovedRefundAudit()
    {
        var store = new Store(); var provider = new Provider(); var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); budget.Aprovar(); store.Items.Add(budget);
        var payment = budget.Pagamento!; payment.IniciarCheckout("https://checkout.example/1", "preference-1"); payment.AplicarEstado(StatusPagamento.Capturado, "provider-1");
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        using var client = factory.CreateClient();
        var route = $"/api/v1/pagamentos/{payment.Id}/reembolsos"; var body = new { valor = 75m, referencia = "ticket-42" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(route, body)).StatusCode);
        Authorized(client, "Atendente");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, body)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Role"); client.DefaultRequestHeaders.Add("X-Test-Role", "Administrador");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/pagamentos/{Guid.NewGuid()}/reembolsos", body)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(route, new { valor = 0m, referencia = "ticket-43" })).StatusCode);
        var response = await client.PostAsJsonAsync(route, body); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(75m, result.GetProperty("valor").GetDecimal()); Assert.Equal("test-user", result.GetProperty("aprovador").GetString());
        Assert.Equal("ticket-42", result.GetProperty("referencia").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, body)).StatusCode); Assert.Equal(1, provider.RefundCalls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { valor = 175.01m, referencia = "ticket-over" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { valor = 175m, referencia = "ticket-final" })).StatusCode);
        Assert.Equal(StatusPagamento.Reembolsado, payment.Status); Assert.Equal(2, payment.Reembolsos.Count);

        var fullBudget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); fullBudget.Aprovar(); store.Items.Add(fullBudget);
        var fullPayment = fullBudget.Pagamento!; fullPayment.IniciarCheckout("https://checkout.example/2", "preference-2"); fullPayment.AplicarEstado(StatusPagamento.Capturado, "provider-2");
        var fullResponse = await client.PostAsJsonAsync($"/api/v1/pagamentos/{fullPayment.Id}/reembolsos", new { valor = 250m, referencia = "ticket-full" });
        Assert.Equal(HttpStatusCode.Created, fullResponse.StatusCode); Assert.Equal(StatusPagamento.Reembolsado, fullPayment.Status);
    }

    [Fact]
    public async Task DependencyRetryExhaustion()
    {
        var attempts = 0; var exhausted = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DependencyRetry.ExecuteAsync(
            _ => { attempts++; return Task.FromException(new InvalidOperationException("transient")); },
            _ => { exhausted++; return Task.CompletedTask; }, CancellationToken.None, 3, _ => TimeSpan.Zero));
        Assert.Equal(3, attempts); Assert.Equal(1, exhausted);
        Assert.Equal(TimeSpan.FromMilliseconds(100), RetryDelayPolicy.Calculate(1, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(299), RetryDelayPolicy.Calculate(2, 99));
        Assert.Equal(TimeSpan.FromMilliseconds(5000), RetryDelayPolicy.Calculate(10, 0));
        var sqs = new RecordingSqs(); var alerts = new Alerts();
        var publisher = new SqsResultPublisher(sqs,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sqs:QueueUrl"] = "main", ["Sqs:DeadLetterQueueUrl"] = "dlq" }).Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqsResultPublisher>.Instance, alerts);
        await publisher.PublicarAsync(MensagensSaga.ResultadoPagamento(Guid.NewGuid(), Guid.NewGuid(), StatusPagamento.Capturado, "provider-1", DateTime.UtcNow), CancellationToken.None);
        Assert.Equal(5, sqs.MainAttempts); Assert.Equal(1, sqs.DeadLetterAttempts); Assert.Equal(1, alerts.Count);
    }

    [Fact]
    public async Task TelemetryExports()
    {
        var resource = TelemetriaBilling.Configurar(ResourceBuilder.CreateEmpty(), "billing-test", "Testing").Build();
        Assert.Equal("billing-test", resource.Attributes.Single(a => a.Key == "service.name").Value);
        Assert.Equal("Testing", resource.Attributes.Single(a => a.Key == "deployment.environment.name").Value);

        var capture = new TelemetryCapture();
        using var factory = new BillingApiFactory(new Store(), new Provider(), new Publisher(), new Alerts(), new Readiness(true), capture);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/orcamentos", null)).StatusCode);
        factory.Services.GetRequiredService<ILogger<BillingLifecycleTests>>().LogInformation("telemetry probe");
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        factory.Services.GetRequiredService<MeterProvider>().ForceFlush();
        factory.Services.GetRequiredService<OpenTelemetry.Logs.LoggerProvider>().ForceFlush();
        Assert.Contains(capture.Spans, s => s.Kind == ActivityKind.Server);
        Assert.Contains(capture.Metrics, m => m.Name == "http.server.request.duration");
        Assert.Contains(capture.Logs, l => l.FormattedMessage == "telemetry probe");
    }

    private sealed class RecordingSqs() : AmazonSQSClient(new AnonymousAWSCredentials(), new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public int MainAttempts { get; private set; }
        public int DeadLetterAttempts { get; private set; }
        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            if (request.QueueUrl == "main") { MainAttempts++; return Task.FromException<SendMessageResponse>(new InvalidOperationException("queue unavailable")); }
            DeadLetterAttempts++; return Task.FromResult(new SendMessageResponse());
        }
    }

    private sealed class TelemetryCapture
    {
        public List<Activity> Spans { get; } = [];
        public List<OpenTelemetry.Metrics.Metric> Metrics { get; } = [];
        public List<LogRecord> Logs { get; } = [];
    }

    private sealed class BillingApiFactory(Store store, ICheckoutProvider provider, Publisher publisher, Alerts alerts, IOrderReadiness readiness, TelemetryCapture? telemetry = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MercadoPago:WebhookSecret"] = "webhook-test-secret",
                ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:1",
            }));
            builder.ConfigureServices(services =>
            {
                if (telemetry is not null)
                {
                    services.AddOpenTelemetry()
                        .WithTracing(tracing => tracing.AddInMemoryExporter(telemetry.Spans))
                        .WithMetrics(metrics => metrics.AddInMemoryExporter(telemetry.Metrics));
                    services.Configure<OpenTelemetryLoggerOptions>(options => options.AddInMemoryExporter(telemetry.Logs));
                }
                services.RemoveAll<IBillingStore>(); services.AddSingleton<IBillingStore>(store);
                services.RemoveAll<ICheckoutProvider>(); services.AddSingleton<ICheckoutProvider>(provider);
                services.RemoveAll<IResultadoPublisher>(); services.AddSingleton<IResultadoPublisher>(publisher);
                services.RemoveAll<IAlertSink>(); services.AddSingleton<IAlertSink>(alerts);
                services.RemoveAll<IOrderReadiness>(); services.AddSingleton<IOrderReadiness>(readiness);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; options.DefaultScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.Role, role) };
            if (!Request.Headers.ContainsKey("X-Test-NoSub")) { claims.Add(new(ClaimTypes.NameIdentifier, "test-user")); claims.Add(new("sub", "test-user")); }
            if (Guid.TryParse(Request.Headers["X-Test-Cliente"].FirstOrDefault(), out var clienteId)) claims.Add(new Claim("clienteId", clienteId.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
