using AutoReparos.Billing.Api;
using AutoReparos.Billing.Application;
using AutoReparos.Billing.Domain;
using AutoReparos.Billing.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testcontainers.PostgreSql;

namespace AutoReparos.Billing.Tests;

public sealed partial class BillingLifecycleTests
{
    private const string WebhookSecret = "webhook-test-secret";

    private static (Store Store, Orcamento Budget, Pagamento Payment) SeedPayment(Guid? clienteId = null)
    {
        var store = new Store(); var budget = new Orcamento(Guid.NewGuid(), clienteId ?? Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); budget.Aprovar(); store.Items.Add(budget);
        return (store, budget, budget.Pagamento!);
    }

    private static Task<HttpResponseMessage> SendWebhook(HttpClient client, string dataId = "payment-1", string requestId = "request-1")
    {
        const string timestamp = "1712345678";
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"id:{dataId};request-id:{requestId};ts:{timestamp};")));
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/mercado-pago") { Content = JsonContent.Create(new { data = new { id = dataId } }) };
        message.Headers.Add("x-signature", $"ts={timestamp},v1={hash}"); message.Headers.Add("x-request-id", requestId);
        return client.SendAsync(message);
    }

    private sealed class FakeHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(respond(Calls)); }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    private static MercadoPagoClient MercadoPago(FakeHandler handler, Alerts alerts) =>
        new(new HttpClient(handler), Config(("MercadoPago:AccessToken", "token")), alerts, _ => TimeSpan.Zero);

    private sealed class ThrowingReadiness(Exception error) : IOrderReadiness
    { public Task<bool> ServicoConcluidoAsync(Guid id, CancellationToken ct) => Task.FromException<bool>(error); }

    // --- C1/C3/C5: vinculo cliente-dono no checkout (achado 1)

    [Fact]
    public async Task ManualRetryAfterTerminalStateClienteOwnership()
    {
        var owner = Guid.NewGuid(); var (store, _, payment) = SeedPayment(owner); var provider = new Provider();
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        using var stranger = Authorized(factory.CreateClient(), "Cliente", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(0, provider.Checkouts); Assert.Equal(StatusPagamento.SemTentativa, payment.Status);
        using var dono = Authorized(factory.CreateClient(), "Cliente", owner);
        Assert.Equal(HttpStatusCode.OK, (await dono.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(1, provider.Checkouts); Assert.Equal(StatusPagamento.Pendente, payment.Status);
    }

    // --- C4: mapeamento de status do Mercado Pago (achado 2)

    [Theory]
    [InlineData("approved", StatusPagamento.Capturado)]
    [InlineData("rejected", StatusPagamento.Rejeitado)]
    [InlineData("cancelled", StatusPagamento.Cancelado)]
    [InlineData("pending", StatusPagamento.Pendente)]
    [InlineData("in_process", StatusPagamento.Pendente)]
    [InlineData("refunded", StatusPagamento.Reembolsado)]
    public async Task WebhookSignatureValidationMapsProviderStatus(string providerStatus, StatusPagamento expected)
    {
        var paymentId = Guid.NewGuid();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, $$"""{"external_reference":"{{paymentId}}","status":"{{providerStatus}}"}"""));
        var result = await MercadoPago(handler, new Alerts()).ConsultarPagamentoAsync("mp-1", CancellationToken.None);
        Assert.Equal((paymentId, expected, "mp-1"), result);
    }

    [Fact]
    public async Task WebhookSignatureValidationHandlesUnknownProviderStatusSafely()
    {
        var unknown = new FakeHandler(_ => Json(HttpStatusCode.OK, $$"""{"external_reference":"{{Guid.NewGuid()}}","status":"something_new"}"""));
        Assert.Null(await MercadoPago(unknown, new Alerts()).ConsultarPagamentoAsync("mp-1", CancellationToken.None));
        var foreign = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"external_reference":"not-a-guid","status":"approved"}"""));
        Assert.Null(await MercadoPago(foreign, new Alerts()).ConsultarPagamentoAsync("mp-1", CancellationToken.None));
    }

    [Fact]
    public async Task WebhookSignatureValidationUnknownProviderStatusDoesNotChangePayment()
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1");
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, $$"""{"external_reference":"{{payment.Id}}","status":"something_new"}"""));
        var publisher = new Publisher();
        using var factory = new BillingApiFactory(store, MercadoPagoAsProvider(handler), publisher, new Alerts(), new Readiness(true));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendWebhook(client)).StatusCode);
        Assert.Equal(StatusPagamento.Pendente, payment.Status); Assert.Empty(publisher.Events);
    }

    private static ICheckoutProvider MercadoPagoAsProvider(FakeHandler handler) => MercadoPago(handler, new Alerts());

    // --- C8: retry com backoff e jitter nas integracoes HTTP (achados 2 e 3)

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task DependencyRetryExhaustionMercadoPagoTransientStatus(HttpStatusCode status)
    {
        var alerts = new Alerts(); var handler = new FakeHandler(_ => Json(status, "{}"));
        var error = await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => MercadoPago(handler, alerts).ConsultarPagamentoAsync("mp-1", CancellationToken.None));
        Assert.Equal("mercado-pago", error.Dependencia); Assert.Equal(5, handler.Calls); Assert.Equal(1, alerts.Count);
        var checkoutHandler = new FakeHandler(_ => Json(status, "{}"));
        await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => MercadoPago(checkoutHandler, alerts).CriarCheckoutAsync(Guid.NewGuid(), 10m, "checkout-key", CancellationToken.None));
        Assert.Equal(5, checkoutHandler.Calls); Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public async Task DependencyRetryExhaustionMercadoPagoPermanentFailureIsNotRetried()
    {
        var alerts = new Alerts(); var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, "{}"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => MercadoPago(handler, alerts).ConsultarPagamentoAsync("mp-1", CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode); Assert.Equal(1, handler.Calls); Assert.Equal(0, alerts.Count);
    }

    [Fact]
    public async Task DependencyRetryExhaustionMercadoPagoRecoversFromTransientFailure()
    {
        var paymentId = Guid.NewGuid(); var alerts = new Alerts();
        var handler = new FakeHandler(call => call < 3 ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, $$"""{"external_reference":"{{paymentId}}","status":"approved"}"""));
        var result = await MercadoPago(handler, alerts).ConsultarPagamentoAsync("mp-1", CancellationToken.None);
        Assert.Equal(StatusPagamento.Capturado, result!.Value.Status); Assert.Equal(3, handler.Calls); Assert.Equal(0, alerts.Count);
    }

    private static OsReadinessClient OsReadiness(FakeHandler handler, Alerts alerts) =>
        new(new HttpClient(handler), Config(("OsService:BaseUrl", "http://os.test")), alerts, _ => TimeSpan.Zero);

    [Fact]
    public async Task DependencyRetryExhaustionOsReadinessRetriesThenAlerts()
    {
        var alerts = new Alerts(); var handler = new FakeHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var error = await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => OsReadiness(handler, alerts).ServicoConcluidoAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("os-service", error.Dependencia); Assert.Equal(5, handler.Calls); Assert.Equal(1, alerts.Count);
        var notFound = new FakeHandler(_ => Json(HttpStatusCode.NotFound, "{}")); var quiet = new Alerts();
        await Assert.ThrowsAsync<HttpRequestException>(() => OsReadiness(notFound, quiet).ServicoConcluidoAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(1, notFound.Calls); Assert.Equal(0, quiet.Count);
    }

    [Fact]
    public async Task DependencyRetryExhaustionOsReadinessTransientFailureDoesNotBreakCheckout()
    {
        var (store, _, payment) = SeedPayment(); var provider = new Provider(); var alerts = new Alerts();
        var flaky = new FakeHandler(call => call < 3 ? Json(HttpStatusCode.ServiceUnavailable, "{}") : Json(HttpStatusCode.OK, """{"status":"Concluida"}"""));
        using (var factory = new BillingApiFactory(store, provider, new Publisher(), alerts, OsReadiness(flaky, alerts)))
        {
            using var client = Authorized(factory.CreateClient());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        }
        Assert.Equal(3, flaky.Calls); Assert.Equal(1, provider.Checkouts); Assert.Equal(0, alerts.Count);

        var (store2, _, payment2) = SeedPayment(); var provider2 = new Provider(); var alerts2 = new Alerts();
        var down = new FakeHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        using var factory2 = new BillingApiFactory(store2, provider2, new Publisher(), alerts2, OsReadiness(down, alerts2));
        using var client2 = Authorized(factory2.CreateClient());
        var response = await client2.PostAsync($"/api/v1/pagamentos/{payment2.Id}/checkout", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dependencia_indisponivel", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(5, down.Calls); Assert.Equal(1, alerts2.Count); Assert.Equal(0, provider2.Checkouts); Assert.Equal(StatusPagamento.SemTentativa, payment2.Status);
    }

    [Fact]
    public async Task DependencyRetryExhaustionWebhookLookupAnswers503()
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1");
        var alerts = new Alerts(); var down = new FakeHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"));
        using var factory = new BillingApiFactory(store, MercadoPago(down, alerts), new Publisher(), alerts, new Readiness(true));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendWebhook(client)).StatusCode);
        Assert.Equal(StatusPagamento.Pendente, payment.Status); Assert.Equal(1, alerts.Count);
    }

    // --- C7: validacao 422 (achado 4)

    [Fact]
    public async Task ApprovedRefundAuditRejectsInvalidAmountAndApprover()
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1"); payment.AplicarEstado(StatusPagamento.Capturado, "provider-1");
        var provider = new Provider(); using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        using var client = Authorized(factory.CreateClient(), "Administrador"); var route = $"/api/v1/pagamentos/{payment.Id}/reembolsos";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(route, new { valor = 10.005m, referencia = "ticket-decimals" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(route, new { valor = 10m, referencia = " " })).StatusCode);
        using var noSub = Authorized(factory.CreateClient(), "Administrador"); noSub.DefaultRequestHeaders.Add("X-Test-NoSub", "1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await noSub.PostAsJsonAsync(route, new { valor = 10m, referencia = "ticket-no-approver" })).StatusCode);
        Assert.Equal(0, provider.RefundCalls); Assert.Empty(payment.Reembolsos); Assert.Equal(StatusPagamento.Capturado, payment.Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { valor = 10.5m, referencia = "ticket-ok" })).StatusCode);
    }

    [Fact]
    public async Task ApprovedRefundAuditCompensatesReservationWhenProviderFails()
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1"); payment.AplicarEstado(StatusPagamento.Capturado, "provider-1");
        var service = new BillingService(store, new FailingRefundProvider(), new Publisher(), new Alerts(), new Readiness(true));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.ReembolsarAsync(payment.Id, 250m, "admin", "ticket-1", CancellationToken.None));
        Assert.Empty(payment.Reembolsos); Assert.Equal(StatusPagamento.Capturado, payment.Status);
    }

    private sealed class FailingRefundProvider : ICheckoutProvider
    {
        public Task<AbrirCheckout> CriarCheckoutAsync(Guid pagamentoId, decimal valor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ReembolsarAsync(string referencia, decimal valor, string idempotencyKey, CancellationToken ct) => Task.FromException<string>(new HttpRequestException("provider refused", null, HttpStatusCode.UnprocessableEntity));
        public Task<(Guid PagamentoId, StatusPagamento Status, string Referencia)?> ConsultarPagamentoAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ScriptedProvider(Func<int, string, Task<string>> onRefund) : ICheckoutProvider
    {
        public int RefundCalls { get; private set; }
        public List<string> Keys { get; } = [];
        public Task<AbrirCheckout> CriarCheckoutAsync(Guid pagamentoId, decimal valor, string idempotencyKey, CancellationToken ct) => throw new RespostaProvedorInvalidaException("mercado-pago", "campo 'init_point' ausente ou inválido.");
        public Task<string> ReembolsarAsync(string referencia, decimal valor, string idempotencyKey, CancellationToken ct) { RefundCalls++; Keys.Add(idempotencyKey); return onRefund(RefundCalls, idempotencyKey); }
        public Task<(Guid PagamentoId, StatusPagamento Status, string Referencia)?> ConsultarPagamentoAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ProcessCrash : Exception;

    private static (Store Store, Pagamento Payment) CapturedPayment()
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1"); payment.AplicarEstado(StatusPagamento.Capturado, "provider-1");
        return (store, payment);
    }

    // --- C7: reembolso seguro contra queda e contra compensacao indevida (achado N1)

    [Fact]
    public async Task ApprovedRefundAuditCrashBetweenReservationAndProviderCallKeepsPendingReservation()
    {
        var (store, payment) = CapturedPayment(); var alerts = new Alerts();
        var provider = new ScriptedProvider((call, _) => call == 1 ? throw new ProcessCrash() : Task.FromResult("refund-1"));
        var service = new BillingService(store, provider, new Publisher(), alerts, new Readiness(true));
        await Assert.ThrowsAsync<ProcessCrash>(() => service.ReembolsarAsync(payment.Id, 250m, "admin", "ticket-crash", CancellationToken.None));
        var reserved = Assert.Single(payment.Reembolsos);
        Assert.Equal(EstadoReembolso.Pendente, reserved.Estado); Assert.Equal(StatusPagamento.Capturado, payment.Status); Assert.Equal(1, alerts.Count);
        // Retomada com a mesma referencia: mesma chave, sem segundo registro, e o pagamento passa a Reembolsado.
        var resumed = await service.ReembolsarAsync(payment.Id, 250m, "admin", "ticket-crash", CancellationToken.None);
        Assert.Same(reserved, resumed); Assert.Single(payment.Reembolsos); Assert.Equal(EstadoReembolso.Confirmado, resumed.Estado);
        Assert.Equal(StatusPagamento.Reembolsado, payment.Status); Assert.Equal(2, provider.RefundCalls); Assert.Single(provider.Keys.Distinct());
    }

    [Fact]
    public async Task ApprovedRefundAuditTimeoutThenResumeWithSameReferenceDoesNotDuplicateRefund()
    {
        var (store, payment) = CapturedPayment(); var alerts = new Alerts(); var keys = new List<string>(); var succeeded = 0; var timeouts = true;
        var handler = new FakeHandler(_ =>
        {
            if (timeouts) throw new TaskCanceledException();
            succeeded++; return Json(HttpStatusCode.Created, """{"id":987}""");
        });
        var client = new MercadoPagoClient(new HttpClient(new RecordingHandler(handler, keys)), Config(("MercadoPago:AccessToken", "token")), alerts, _ => TimeSpan.Zero);
        var service = new BillingService(store, client, new Publisher(), alerts, new Readiness(true));
        await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => service.ReembolsarAsync(payment.Id, 250m, "admin", "ticket-timeout", CancellationToken.None));
        var reserved = Assert.Single(payment.Reembolsos);
        Assert.Equal(EstadoReembolso.Pendente, reserved.Estado); Assert.Equal(StatusPagamento.Capturado, payment.Status);
        Assert.Equal(5, handler.Calls); Assert.Equal(2, alerts.Count); // esgotamento do retry + desfecho incerto
        timeouts = false;
        var resumed = await service.ReembolsarAsync(payment.Id, 250m, "admin", "ticket-timeout", CancellationToken.None);
        Assert.Same(reserved, resumed); Assert.Single(payment.Reembolsos); Assert.Equal(1, succeeded);
        Assert.Equal(EstadoReembolso.Confirmado, resumed.Estado); Assert.Equal(StatusPagamento.Reembolsado, payment.Status);
        Assert.Single(keys.Distinct()); Assert.Equal(6, handler.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReembolsarAsync(payment.Id, 250m, "admin", "ticket-timeout", CancellationToken.None));
        Assert.Equal(6, handler.Calls);
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, List<string> keys) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { keys.Add(request.Headers.GetValues("X-Idempotency-Key").Single()); return base.SendAsync(request, cancellationToken); }
    }

    [Fact]
    public async Task ApprovedRefundAuditNewReferenceWhilePendingNeverExceedsTotal()
    {
        var (store, payment) = CapturedPayment();
        var provider = new ScriptedProvider((call, _) => call == 1 ? throw new ProcessCrash() : Task.FromResult("refund-ok"));
        var service = new BillingService(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        await Assert.ThrowsAsync<ProcessCrash>(() => service.ReembolsarAsync(payment.Id, 200m, "admin", "ticket-pending", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReembolsarAsync(payment.Id, 100m, "admin", "ticket-new", CancellationToken.None));
        Assert.Equal(1, provider.RefundCalls); Assert.Single(payment.Reembolsos);
        await service.ReembolsarAsync(payment.Id, 50m, "admin", "ticket-fits", CancellationToken.None);
        Assert.Equal(StatusPagamento.Capturado, payment.Status); // o Pendente de 200 ainda nao foi confirmado
        await service.ReembolsarAsync(payment.Id, 200m, "admin", "ticket-pending", CancellationToken.None);
        Assert.Equal(250m, payment.Reembolsos.Sum(r => r.Valor)); Assert.Equal(StatusPagamento.Reembolsado, payment.Status);
    }

    [Fact]
    public async Task ApprovedRefundAuditCancellationKeepsReservationPending()
    {
        var (store, payment) = CapturedPayment(); var alerts = new Alerts();
        var provider = new ScriptedProvider((_, _) => throw new OperationCanceledException());
        var service = new BillingService(store, provider, new Publisher(), alerts, new Readiness(true));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReembolsarAsync(payment.Id, 10m, "admin", "ticket-cancel", CancellationToken.None));
        Assert.Equal(EstadoReembolso.Pendente, Assert.Single(payment.Reembolsos).Estado); Assert.Equal(1, alerts.Count);
    }

    [Fact]
    public async Task ApprovedRefundAuditPersistsPendingConfirmsAndCompensatesInPostgres()
    {
        await using var postgres = await StartPostgresAsync(); var gate = new SaveGate(); var alerts = new Alerts();
        var provider = new ScriptedProvider((call, _) => call switch
        {
            1 => throw new DependenciaIndisponivelException("mercado-pago"),
            2 => Task.FromResult("refund-2"),
            _ => throw new HttpRequestException("provider refused", null, HttpStatusCode.UnprocessableEntity),
        });
        using var factory = new DbApiFactory(postgres.GetConnectionString(), gate, provider, new Publisher(), alerts, new Readiness(true));
        await MigrateAsync(factory); var (_, paymentId, _) = await SeedApprovedBudgetInDb(factory, capture: true);
        using var client = Authorized(factory.CreateClient()); var route = $"/api/v1/pagamentos/{paymentId}/reembolsos";
        Task<List<Reembolso>> Rows() => InDbAsync(factory, db => db.Reembolsos.AsNoTracking().Where(r => r.PagamentoId == paymentId).ToListAsync());
        Task<StatusPagamento> PaymentStatus() => InDbAsync(factory, async db => (await db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Status);
        // 1) reserva persistida como Pendente e provedor incerto: 503, nada desfeito
        var uncertain = await client.PostAsJsonAsync(route, new { valor = 100m, referencia = "ticket-1" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, uncertain.StatusCode); Assert.Equal(1, alerts.Count);
        var pending = Assert.Single(await Rows()); Assert.Equal(EstadoReembolso.Pendente, pending.Estado); Assert.Equal(100m, pending.Valor);
        Assert.Equal(StatusPagamento.Capturado, await PaymentStatus());
        // 2) retomada com a mesma referencia confirma a mesma linha
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { valor = 100m, referencia = "ticket-1" })).StatusCode);
        var confirmed = Assert.Single(await Rows()); Assert.Equal(pending.Id, confirmed.Id); Assert.Equal(EstadoReembolso.Confirmado, confirmed.Estado);
        Assert.Equal(provider.Keys[0], provider.Keys[1]);
        // 3) falha definitiva (4xx) compensa: a linha reservada some
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync(route, new { valor = 50m, referencia = "ticket-2" })).StatusCode);
        Assert.Equal(confirmed.Id, Assert.Single(await Rows()).Id); Assert.Equal(StatusPagamento.Capturado, await PaymentStatus());
    }

    [Fact]
    public async Task ApprovedRefundAuditInvalidProviderResponseKeepsPendingAndReturns502()
    {
        var (store, payment) = CapturedPayment(); var alerts = new Alerts();
        var provider = new ScriptedProvider((_, _) => throw new RespostaProvedorInvalidaException("mercado-pago", "campo 'id' ausente ou inválido."));
        using var factory = new BillingApiFactory(store, provider, new Publisher(), alerts, new Readiness(true)); using var client = Authorized(factory.CreateClient());
        var response = await client.PostAsJsonAsync($"/api/v1/pagamentos/{payment.Id}/reembolsos", new { valor = 10m, referencia = "ticket-invalid-body" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("resposta_provedor_invalida", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(EstadoReembolso.Pendente, Assert.Single(payment.Reembolsos).Estado); Assert.Equal(1, alerts.Count);
    }

    [Fact]
    public async Task CreatesSingleCheckoutAttemptInvalidProviderResponseReturns502NotFound()
    {
        var (store, _, payment) = SeedPayment(); var provider = new ScriptedProvider((_, _) => Task.FromResult("x"));
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true)); using var client = Authorized(factory.CreateClient());
        var response = await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("resposta_provedor_invalida", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(StatusPagamento.SemTentativa, payment.Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"init_point":"https://x"}""")]
    [InlineData("not-json")]
    public async Task DependencyRetryExhaustionInvalidProviderBodyIsNotRetried(string body)
    {
        var alerts = new Alerts(); var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, body));
        await Assert.ThrowsAsync<RespostaProvedorInvalidaException>(() => MercadoPago(handler, alerts).CriarCheckoutAsync(Guid.NewGuid(), 10m, "checkout-key", CancellationToken.None));
        await Assert.ThrowsAsync<RespostaProvedorInvalidaException>(() => MercadoPago(handler, alerts).ReembolsarAsync("p", 1m, "k", CancellationToken.None));
        Assert.Equal(2, handler.Calls); Assert.Equal(0, alerts.Count);
        var missingStatus = new FakeHandler(_ => Json(HttpStatusCode.OK, $$"""{"external_reference":"{{Guid.NewGuid()}}"}"""));
        await Assert.ThrowsAsync<RespostaProvedorInvalidaException>(() => MercadoPago(missingStatus, alerts).ConsultarPagamentoAsync("p", CancellationToken.None));
    }

    // --- C8: timeout do HttpClient e transitorio; cancelamento do chamador propaga (achado N2)

    [Fact]
    public async Task DependencyRetryExhaustionTimeoutCountsAsTransientFailure()
    {
        var alerts = new Alerts(); var handler = new FakeHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var error = await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => MercadoPago(handler, alerts).ConsultarPagamentoAsync("mp-1", CancellationToken.None));
        Assert.Equal("mercado-pago", error.Dependencia); Assert.Equal(5, handler.Calls); Assert.Equal(1, alerts.Count);
        var recovering = new FakeHandler(call => call < 3 ? throw new TaskCanceledException() : Json(HttpStatusCode.OK, $$"""{"external_reference":"{{Guid.NewGuid()}}","status":"approved"}"""));
        var result = await MercadoPago(recovering, alerts).ConsultarPagamentoAsync("mp-1", CancellationToken.None);
        Assert.Equal(StatusPagamento.Capturado, result!.Value.Status); Assert.Equal(3, recovering.Calls); Assert.Equal(1, alerts.Count);
        var os = new FakeHandler(_ => throw new TaskCanceledException());
        await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => OsReadiness(os, alerts).ServicoConcluidoAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(5, os.Calls); Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public async Task DependencyRetryExhaustionCallerCancellationPropagatesWithoutRetry()
    {
        using var cts = new CancellationTokenSource(); var alerts = new Alerts();
        var handler = new FakeHandler(_ => { cts.Cancel(); throw new TaskCanceledException(null, null, cts.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MercadoPago(handler, alerts).ConsultarPagamentoAsync("mp-1", cts.Token));
        Assert.Equal(1, handler.Calls); Assert.Equal(0, alerts.Count);
    }

    // --- C1/C7: tetos de valor (achado N3)

    [Fact]
    public async Task CreatesBudgetForOrderRejectsValueAboveNumericCeiling()
    {
        var store = new Store(); using var factory = new BillingApiFactory(store, new Provider(), new Publisher(), new Alerts(), new Readiness(true)); using var client = Authorized(factory.CreateClient(), "ServicoOS");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId = Guid.NewGuid(), valor = 10_000_000_000_000_000m })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId = Guid.NewGuid(), valor = decimal.MaxValue })).StatusCode);
        Assert.Empty(store.Items);
        Assert.Throws<ArgumentException>(() => new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 10_000_000_000_000_000m, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId = Guid.NewGuid(), valor = 9_999_999_999_999_999.99m })).StatusCode);
    }

    [Fact]
    public async Task ApprovedRefundAuditHugeAmountReturns422WithoutOverflow()
    {
        var (store, payment) = CapturedPayment(); var provider = new Provider();
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true)); using var client = Authorized(factory.CreateClient()); var route = $"/api/v1/pagamentos/{payment.Id}/reembolsos";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, new { valor = 100m, referencia = "ticket-partial" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(route, new { valor = decimal.MaxValue, referencia = "ticket-max" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(route, new { valor = 250.01m, referencia = "ticket-above-total" })).StatusCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => payment.Reembolsar(decimal.MaxValue, "admin", "ticket-domain", DateTimeOffset.UtcNow));
        Assert.Equal(1, provider.RefundCalls); Assert.Single(payment.Reembolsos);
    }

    // --- C5: webhook fora de ordem nao reabre pagamento terminal (achado 5)

    [Theory]
    [InlineData(StatusPagamento.Rejeitado)]
    [InlineData(StatusPagamento.Cancelado)]
    [InlineData(StatusPagamento.Expirado)]
    public async Task ManualRetryAfterTerminalStateIgnoresOutOfOrderPending(StatusPagamento terminal)
    {
        var (store, _, payment) = SeedPayment(); var provider = new Provider(); var publisher = new Publisher();
        using var factory = new BillingApiFactory(store, provider, publisher, new Alerts(), new Readiness(true));
        using var admin = Authorized(factory.CreateClient()); using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        provider.WebhookResult = (payment.Id, terminal, "mp-1");
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(anonymous)).StatusCode);
        Assert.Equal(terminal, payment.Status); Assert.Single(publisher.Events);
        provider.WebhookResult = (payment.Id, StatusPagamento.Pendente, "mp-1");
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(anonymous, requestId: "request-2")).StatusCode);
        Assert.Equal(terminal, payment.Status); Assert.Single(publisher.Events);
        Assert.False(payment.AplicarEstado(StatusPagamento.Pendente, "mp-1"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(2, provider.Checkouts); Assert.Equal(StatusPagamento.Pendente, payment.Status);
    }

    // --- C4: idempotencia em mais de um estado (achado 8)

    [Theory]
    [InlineData(StatusPagamento.Capturado)]
    [InlineData(StatusPagamento.Rejeitado)]
    [InlineData(StatusPagamento.Cancelado)]
    [InlineData(StatusPagamento.Expirado)]
    public async Task WebhookSignatureValidationDuplicateDeliveryPublishesOnce(StatusPagamento status)
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1");
        var provider = new Provider { WebhookResult = (payment.Id, status, "mp-payment-1") }; var publisher = new Publisher();
        using var factory = new BillingApiFactory(store, provider, publisher, new Alerts(), new Readiness(true));
        using var client = factory.CreateClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(client)).StatusCode);
        Assert.Equal(status, payment.Status); Assert.Single(publisher.Events); Assert.Equal(status.ToString(), publisher.Events[0].Status);
    }

    [Fact]
    public async Task WebhookSignatureValidationRedeliveryAfterPublishFailureRepublishes()
    {
        var (store, _, payment) = SeedPayment(); payment.IniciarCheckout("https://checkout.example/1", "preference-1");
        var provider = new Provider { WebhookResult = (payment.Id, StatusPagamento.Rejeitado, "mp-payment-1") }; var publisher = new Publisher(failAttempts: 1); var alerts = new Alerts();
        using var factory = new BillingApiFactory(store, provider, publisher, alerts, new Readiness(true));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendWebhook(client)).StatusCode);
        Assert.Equal(StatusPagamento.Rejeitado, payment.Status); Assert.Empty(publisher.Events); Assert.Equal(1, alerts.Count);
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(client)).StatusCode);
        Assert.Single(publisher.Events); Assert.True(payment.ResultadoPublicado);
    }

    // --- C2: servico concluido, orcamento nao aprovado (achado 9)

    [Fact]
    public async Task ChargeRequiresApprovalAndCompletionBlocksUnapprovedBudget()
    {
        var store = new Store(); var provider = new Provider(); var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow);
        var payment = new Pagamento(budget.Id, budget.Valor); budget.DefinirPagamento(payment); store.Items.Add(budget);
        using var factory = new BillingApiFactory(store, provider, new Publisher(), new Alerts(), new Readiness(true));
        using var client = Authorized(factory.CreateClient());
        Assert.Equal(StatusOrcamento.Pendente, budget.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(0, provider.Checkouts); Assert.Equal(StatusPagamento.SemTentativa, payment.Status);
        budget.Aprovar();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        Assert.Equal(1, provider.Checkouts);
    }

    // --- C9: resource aplicado pelo Program real e requisicao 2xx com coletor inalcancavel (achado 7)

    [Fact]
    public async Task TelemetryExportsHostResourceAndSuccessWithUnreachableCollector()
    {
        var capture = new TelemetryCapture(); var store = new Store();
        using var factory = new BillingApiFactory(store, new Provider(), new Publisher(), new Alerts(), new Readiness(true), capture);
        using var client = Authorized(factory.CreateClient(), "ServicoOS");
        var created = await client.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId = Guid.NewGuid(), valor = 99.9m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Single(store.Items);

        void AssertResource(BaseProvider provider)
        {
            var attributes = provider.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
            Assert.Equal("autoreparos-billing", attributes["service.name"]);
            Assert.Equal("Testing", attributes["deployment.environment.name"]);
        }
        AssertResource(factory.Services.GetRequiredService<TracerProvider>());
        AssertResource(factory.Services.GetRequiredService<MeterProvider>());
        AssertResource(factory.Services.GetRequiredService<OpenTelemetry.Logs.LoggerProvider>());
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        Assert.Contains(capture.Spans, s => s.Kind == System.Diagnostics.ActivityKind.Server && s.GetTagItem("http.response.status_code") is 201);
    }

    // --- Concorrencia otimista com PostgreSQL real (achado 6)

    private sealed class SaveGate : SaveChangesInterceptor
    {
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _expected, _arrived;
        public void Arm(int expected) { _release = new(TaskCreationOptions.RunContinuationsAsynchronously); _arrived = 0; _expected = expected; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var expected = _expected;
            if (expected > 0 && Interlocked.Increment(ref _arrived) <= expected)
            {
                var release = _release;
                if (_arrived == expected) release.TrySetResult();
                await Task.WhenAny(release.Task, Task.Delay(TimeSpan.FromSeconds(20), cancellationToken));
            }
            return result;
        }
    }

    private sealed class DbApiFactory(string connectionString, SaveGate gate, ICheckoutProvider provider, Publisher publisher, Alerts alerts, IOrderReadiness readiness) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<BillingDbContext>>();
                services.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<BillingDbContext>));
                services.AddDbContext<BillingDbContext>(o => o.UseNpgsql(connectionString).AddInterceptors(gate));
                services.RemoveAll<ICheckoutProvider>(); services.AddSingleton<ICheckoutProvider>(provider);
                services.RemoveAll<IResultadoPublisher>(); services.AddSingleton<IResultadoPublisher>(publisher);
                services.RemoveAll<IAlertSink>(); services.AddSingleton<IAlertSink>(alerts);
                services.RemoveAll<IOrderReadiness>(); services.AddSingleton<IOrderReadiness>(readiness);
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; options.DefaultScheme = "Test"; })
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }
    }

    private static async Task<PostgreSqlContainer> StartPostgresAsync()
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build(); await container.StartAsync(); return container;
    }

    private static async Task MigrateAsync(DbApiFactory factory)
    {
        using var scope = factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
    }

    private static async Task<T> InDbAsync<T>(DbApiFactory factory, Func<BillingDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<BillingDbContext>());
    }

    private static async Task<(Guid BudgetId, Guid PaymentId, Guid ClienteId)> SeedApprovedBudgetInDb(DbApiFactory factory, bool capture = false)
    {
        var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow); budget.Aprovar();
        if (capture) { budget.Pagamento!.IniciarCheckout("https://checkout.example/1", "preference-1"); budget.Pagamento.AplicarEstado(StatusPagamento.Capturado, "provider-1"); }
        await InDbAsync(factory, async db => { db.Orcamentos.Add(budget); await db.SaveChangesAsync(); return 0; });
        return (budget.Id, budget.Pagamento!.Id, budget.ClienteId);
    }

    [Fact]
    public async Task CreatesSingleCheckoutAttemptUnderConcurrentRequests()
    {
        await using var postgres = await StartPostgresAsync(); var gate = new SaveGate(); var provider = new Provider(); var alerts = new Alerts();
        using var factory = new DbApiFactory(postgres.GetConnectionString(), gate, provider, new Publisher(), alerts, new Readiness(true));
        await MigrateAsync(factory); var (_, paymentId, _) = await SeedApprovedBudgetInDb(factory);
        using var a = Authorized(factory.CreateClient()); using var b = Authorized(factory.CreateClient());
        gate.Arm(2);
        var responses = await Task.WhenAll(a.PostAsync($"/api/v1/pagamentos/{paymentId}/checkout", null), b.PostAsync($"/api/v1/pagamentos/{paymentId}/checkout", null));
        Assert.Equal([200, 409], responses.Select(r => (int)r.StatusCode).Order().ToArray());
        var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("conflito_concorrencia", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        gate.Arm(0);
        var persisted = await InDbAsync(factory, db => db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId));
        Assert.Equal(StatusPagamento.Pendente, persisted.Status); Assert.NotNull(persisted.UrlCheckout);
        // Uma nova chamada depois do conflito devolve a mesma tentativa ativa, sem criar outra.
        var checkoutsBefore = provider.Checkouts;
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync($"/api/v1/pagamentos/{paymentId}/checkout", null)).StatusCode);
        Assert.Equal(checkoutsBefore, provider.Checkouts);
    }

    [Fact]
    public async Task CreatesSingleCheckoutAttemptSharesIdempotencyKeyAndPersistsAttemptCounterManualRetryAfterTerminalState()
    {
        await using var postgres = await StartPostgresAsync(); var gate = new SaveGate(); var provider = new Provider();
        using var factory = new DbApiFactory(postgres.GetConnectionString(), gate, provider, new Publisher(), new Alerts(), new Readiness(true));
        await MigrateAsync(factory); var (_, paymentId, _) = await SeedApprovedBudgetInDb(factory);
        using var a = Authorized(factory.CreateClient()); using var b = Authorized(factory.CreateClient());
        gate.Arm(2);
        var responses = await Task.WhenAll(a.PostAsync($"/api/v1/pagamentos/{paymentId}/checkout", null), b.PostAsync($"/api/v1/pagamentos/{paymentId}/checkout", null));
        gate.Arm(0);
        Assert.Equal([200, 409], responses.Select(r => (int)r.StatusCode).Order().ToArray());
        // As duas chamadas concorrentes da mesma tentativa usaram a mesma chave; so uma tentativa persistiu.
        Assert.Equal(2, provider.CheckoutKeys.Count); Assert.Single(provider.CheckoutKeys.Distinct());
        Assert.Equal($"checkout-{paymentId:N}-1", provider.CheckoutKeys[0]);
        var firstUrl = (await InDbAsync(factory, db => db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId))).UrlCheckout;
        Assert.Equal(1, (await InDbAsync(factory, db => db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId))).Tentativas);
        // Estado terminal e retry manual: chave nova, URL nova, contador persistido em 2.
        await InDbAsync(factory, async db => { var p = await db.Pagamentos.SingleAsync(x => x.Id == paymentId); p.AplicarEstado(StatusPagamento.Rejeitado, "provider-1"); await db.SaveChangesAsync(); return 0; });
        var retry = await a.PostAsync($"/api/v1/pagamentos/{paymentId}/checkout", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(3, provider.CheckoutKeys.Count); Assert.Equal($"checkout-{paymentId:N}-2", provider.CheckoutKeys[2]);
        var persisted = await InDbAsync(factory, db => db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId));
        Assert.Equal(2, persisted.Tentativas); Assert.Equal(StatusPagamento.Pendente, persisted.Status);
        Assert.NotEqual(firstUrl, persisted.UrlCheckout); Assert.Equal("https://checkout.example/2", persisted.UrlCheckout);
    }

    [Fact]
    public async Task ApprovedRefundAuditConcurrentRefundsNeverExceedPaidAmount()
    {
        await using var postgres = await StartPostgresAsync(); var gate = new SaveGate(); var provider = new Provider();
        using var factory = new DbApiFactory(postgres.GetConnectionString(), gate, provider, new Publisher(), new Alerts(), new Readiness(true));
        await MigrateAsync(factory); var (_, paymentId, _) = await SeedApprovedBudgetInDb(factory, capture: true);
        using var a = Authorized(factory.CreateClient()); using var b = Authorized(factory.CreateClient()); var route = $"/api/v1/pagamentos/{paymentId}/reembolsos";
        gate.Arm(2);
        var responses = await Task.WhenAll(a.PostAsJsonAsync(route, new { valor = 150m, referencia = "ticket-a" }), b.PostAsJsonAsync(route, new { valor = 150m, referencia = "ticket-b" }));
        gate.Arm(0);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("conflito_concorrencia", (await loser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(1, provider.RefundCalls);
        var refunded = await InDbAsync(factory, async db => (await db.Reembolsos.AsNoTracking().Where(r => r.PagamentoId == paymentId).ToListAsync()).Sum(r => r.Valor));
        Assert.Equal(150m, refunded);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync(route, new { valor = 150m, referencia = "ticket-c" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync(route, new { valor = 100m, referencia = "ticket-d" })).StatusCode);
        var final = await InDbAsync(factory, async db => (Total: (await db.Reembolsos.AsNoTracking().Where(r => r.PagamentoId == paymentId).ToListAsync()).Sum(r => r.Valor), Status: (await db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId)).Status));
        Assert.Equal(250m, final.Total); Assert.Equal(StatusPagamento.Reembolsado, final.Status);
    }

    [Fact]
    public async Task CreatesBudgetForOrderConcurrentApprovalsCreateSinglePayment()
    {
        await using var postgres = await StartPostgresAsync(); var gate = new SaveGate();
        using var factory = new DbApiFactory(postgres.GetConnectionString(), gate, new Provider(), new Publisher(), new Alerts(), new Readiness(true));
        await MigrateAsync(factory);
        var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow);
        await InDbAsync(factory, async db => { db.Orcamentos.Add(budget); await db.SaveChangesAsync(); return 0; });
        using var a = Authorized(factory.CreateClient(), "Cliente", budget.ClienteId); using var b = Authorized(factory.CreateClient(), "Cliente", budget.ClienteId);
        gate.Arm(2);
        var responses = await Task.WhenAll(a.PostAsync($"/api/v1/orcamentos/{budget.Id}/aprovacao", null), b.PostAsync($"/api/v1/orcamentos/{budget.Id}/aprovacao", null));
        gate.Arm(0);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await InDbAsync(factory, db => db.Pagamentos.CountAsync(p => p.OrcamentoId == budget.Id)));
    }
}
