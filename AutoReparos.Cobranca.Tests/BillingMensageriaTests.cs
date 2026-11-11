using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Cobranca.Api;
using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;
using AutoReparos.Cobranca.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AutoReparos.Cobranca.Tests;

// Contrato de mensagens da Saga (billing): outbox, envelopes, consumidor de cancelamento, deduplicação e retry/DLQ/alerta.
// Todos usam PostgreSQL real (Testcontainers) e SQS falso; nenhum caminho pode chamar estorno ou checkout no provedor.
public sealed partial class BillingLifecycleTests
{
    private sealed class RecordingAlerts : IAlertSink
    {
        private readonly ConcurrentQueue<string> _codes = new();
        public string[] Codes => [.. _codes];
        public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { _codes.Enqueue(codigo); return Task.CompletedTask; }
    }

    private sealed class SaveFail : SaveChangesInterceptor
    {
        private int _remaining, _attempts;
        /// <summary>Quantas gravações seguintes falham (int.MaxValue = indefinidamente; 0 = volta ao normal).</summary>
        public void Fail(int times) => Interlocked.Exchange(ref _remaining, times);
        public int Attempts => Volatile.Read(ref _attempts);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _remaining) > 0)
            {
                if (Volatile.Read(ref _remaining) != int.MaxValue) Interlocked.Decrement(ref _remaining);
                Interlocked.Increment(ref _attempts); throw new InvalidOperationException("banco indisponível");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeSqs() : AmazonSQSClient(new AnonymousAWSCredentials(), new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        private readonly object _lock = new();
        private readonly List<(string Url, string Body, bool Ok)> _attempts = [];
        private readonly Dictionary<string, Queue<Message>> _inbox = [];
        private readonly List<string> _deleted = [];
        /// <summary>(fila, número da tentativa nessa fila) -> deve falhar?</summary>
        public Func<string, int, bool> Fails { get; set; } = (_, _) => false;
        public int AttemptsTo(string url) { lock (_lock) return _attempts.Count(a => a.Url == url); }
        public string[] Delivered(string url) { lock (_lock) return [.. _attempts.Where(a => a.Url == url && a.Ok).Select(a => a.Body)]; }
        public string[] Attempted(string url) { lock (_lock) return [.. _attempts.Where(a => a.Url == url).Select(a => a.Body)]; }
        public string[] Deleted { get { lock (_lock) return [.. _deleted]; } }
        public void Enqueue(string url, string body, string receipt)
        { lock (_lock) { if (!_inbox.TryGetValue(url, out var queue)) _inbox[url] = queue = new(); queue.Enqueue(new Message { Body = body, ReceiptHandle = receipt, MessageId = receipt }); } }
        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var n = _attempts.Count(a => a.Url == request.QueueUrl) + 1; var ok = !Fails(request.QueueUrl, n);
                _attempts.Add((request.QueueUrl, request.MessageBody, ok));
                if (!ok) throw new InvalidOperationException("SQS indisponível");
            }
            return Task.FromResult(new SendMessageResponse());
        }
        public override async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken cancellationToken = default)
        {
            var messages = new List<Message>();
            lock (_lock) { if (_inbox.TryGetValue(request.QueueUrl, out var queue)) while (messages.Count < 10 && queue.TryDequeue(out var m)) messages.Add(m); }
            if (messages.Count == 0) await Task.Delay(20, cancellationToken);
            return new ReceiveMessageResponse { Messages = messages };
        }
        public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default)
        { lock (_lock) _deleted.Add(request.ReceiptHandle); return Task.FromResult(new DeleteMessageResponse()); }
    }

    private sealed class MensageriaFactory(string connectionString, SaveGate gate, SaveFail fail, FakeSqs sqs, RecordingAlerts alerts, Provider provider, List<int> delays) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sqs:QueueUrl"] = "main",
                ["Sqs:DeadLetterQueueUrl"] = "dlq",
                ["Sqs:ComandosQueueUrl"] = "cmd",
                ["Sqs:ComandosDeadLetterQueueUrl"] = "cmd-dlq",
                ["Outbox:IntervaloMs"] = "50",
                ["MercadoPago:WebhookSecret"] = WebhookSecret,
                ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:1",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<BillingDbContext>>();
                services.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<BillingDbContext>));
                services.AddDbContext<BillingDbContext>(o => o.UseNpgsql(connectionString).AddInterceptors(gate, fail));
                services.RemoveAll<IAmazonSQS>(); services.AddSingleton<IAmazonSQS>(sqs);
                services.RemoveAll<ICheckoutProvider>(); services.AddSingleton<ICheckoutProvider>(provider);
                services.RemoveAll<IAlertSink>(); services.AddSingleton<IAlertSink>(alerts);
                services.RemoveAll<IOrderReadiness>(); services.AddSingleton<IOrderReadiness>(new Readiness(true));
                // Backoff sem espera real, registrando o número da tentativa de cada espera.
                services.AddSingleton<Func<int, TimeSpan>>(_ => attempt => { lock (delays) delays.Add(attempt); return TimeSpan.Zero; });
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; options.DefaultScheme = "Test"; })
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        }
    }

    private sealed class PostgreSqlContainerHolder(Testcontainers.PostgreSql.PostgreSqlContainer container) : IAsyncDisposable
    {
        public string ConnectionString => container.GetConnectionString();
        public ValueTask DisposeAsync() => container.DisposeAsync();
    }

    private static async Task<(MensageriaFactory Factory, PostgreSqlContainerHolder Postgres, SaveGate Gate, SaveFail Fail, FakeSqs Sqs, RecordingAlerts Alerts, Provider Provider, List<int> Delays)> StartMensageriaAsync()
    {
        var postgres = new PostgreSqlContainerHolder(await StartPostgresAsync());
        var gate = new SaveGate(); var fail = new SaveFail(); var sqs = new FakeSqs(); var alerts = new RecordingAlerts(); var provider = new Provider(); var delays = new List<int>();
        var factory = new MensageriaFactory(postgres.ConnectionString, gate, fail, sqs, alerts, provider, delays);
        using (var scope = factory.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
        return (factory, postgres, gate, fail, sqs, alerts, provider, delays);
    }

    private static async Task<T> Db<T>(MensageriaFactory factory, Func<BillingDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<BillingDbContext>());
    }

    private static async Task<Orcamento> SeedBudget(MensageriaFactory factory, bool approved = false, bool checkoutStarted = false)
    {
        var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 250m, DateTimeOffset.UtcNow);
        if (approved || checkoutStarted) budget.Aprovar();
        if (checkoutStarted) budget.Pagamento!.IniciarCheckout("https://checkout.example/1", "preference-1");
        await Db(factory, async db => { db.Orcamentos.Add(budget); await db.SaveChangesAsync(); return 0; });
        return budget;
    }

    private static async Task<int> DispatchAsync(MensageriaFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessarPendentesAsync(CancellationToken.None);
    }

    private static Task<DesfechoComando> HandleCommandAsync(MensageriaFactory factory, string body) =>
        factory.Services.GetRequiredService<ProcessadorComandos>().ProcessarAsync(body, CancellationToken.None);

    private static string CancelCommand(Guid eventId, Orcamento budget, Guid? correlacaoId = null, string tipo = "cobranca.cancelar-orcamento.v1") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventId"] = eventId,
            ["tipo"] = tipo,
            ["correlacaoId"] = correlacaoId,
            ["ordemServicoId"] = budget.OrdemServicoId,
            ["ocorreuEm"] = "2026-11-10T12:00:00Z",
            ["orcamentoId"] = budget.Id,
            ["motivo"] = "falha na execucao"
        });

    private static JsonElement Parse(string body) => JsonDocument.Parse(body).RootElement.Clone();
    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    private static void AssertUtcRecent(JsonElement envelope)
    {
        var text = envelope.GetProperty("ocorreuEm").GetString()!;
        Assert.EndsWith("Z", text);
        var when = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, when.Kind); Assert.InRange((DateTime.UtcNow - when).TotalMinutes, -1, 5);
    }

    private static Task<MensagemSaida[]> Outbox(MensageriaFactory factory) =>
        Db(factory, async db => (await db.MensagensSaida.AsNoTracking().ToListAsync()).OrderBy(m => m.CriadoEm).ToArray());

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition()) { if (DateTime.UtcNow > deadline) Assert.Fail($"Tempo esgotado esperando: {what}"); await Task.Delay(25); }
    }

    // --- orcamento.aprovado.v1 (outbox na transacao da aprovacao)

    [Fact]
    public async Task ApprovalWritesOutboxInSameTransactionAndSucceedsWhileSqsIsDown()
    {
        var (factory, postgres, gate, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        sqs.Fails = (_, _) => true;
        var budget = await SeedBudget(factory);
        using var client = Authorized(factory.CreateClient(), "Cliente", budget.ClienteId);

        var response = await client.PostAsync($"/api/v1/orcamentos/{budget.Id}/aprovacao", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Nenhuma chamada ao SQS no caminho HTTP; a mensagem ficou gravada e pendente.
        Assert.Equal(0, sqs.AttemptsTo("main")); Assert.Equal(0, sqs.AttemptsTo("dlq"));
        var persisted = await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == budget.Id));
        Assert.Equal(StatusOrcamento.Aprovado, persisted.Status);
        var rows = await Outbox(factory);
        var row = Assert.Single(rows);
        Assert.Equal("orcamento.aprovado.v1", row.Tipo); Assert.Null(row.EnviadoEm); Assert.Null(row.EsgotadaEm); Assert.Equal(0, row.Tentativas);

        var envelope = Parse(row.Payload);
        Assert.Equal(["eventId", "tipo", "ordemServicoId", "ocorreuEm", "orcamentoId", "clienteId"], Names(envelope));
        Assert.Equal(row.Id, envelope.GetProperty("eventId").GetGuid());
        Assert.Equal("orcamento.aprovado.v1", envelope.GetProperty("tipo").GetString());
        Assert.Equal(budget.OrdemServicoId, envelope.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(budget.Id, envelope.GetProperty("orcamentoId").GetGuid());
        Assert.Equal(budget.ClienteId, envelope.GetProperty("clienteId").GetGuid());
        AssertUtcRecent(envelope);

        // SQS volta: o despachador publica depois, uma unica vez, com o payload exato gravado.
        sqs.Fails = (_, _) => false;
        Assert.Equal(1, await DispatchAsync(factory));
        Assert.Equal([row.Payload], sqs.Delivered("main"));
        var sent = Assert.Single(await Outbox(factory));
        Assert.NotNull(sent.EnviadoEm); Assert.Equal(1, sent.Tentativas);
        Assert.Equal(0, await DispatchAsync(factory)); Assert.Single(sqs.Delivered("main"));
        Assert.Equal(0, provider.RefundCalls); Assert.Equal(0, provider.Checkouts); Assert.Empty(alerts.Codes);
    }

    [Fact]
    public async Task ConcurrentApprovalsWriteSingleOutboxMessage()
    {
        var (factory, postgres, gate, _, sqs, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory);
        using var a = Authorized(factory.CreateClient(), "Cliente", budget.ClienteId); using var b = Authorized(factory.CreateClient(), "Cliente", budget.ClienteId);
        gate.Arm(2);
        var responses = await Task.WhenAll(a.PostAsync($"/api/v1/orcamentos/{budget.Id}/aprovacao", null), b.PostAsync($"/api/v1/orcamentos/{budget.Id}/aprovacao", null));
        gate.Arm(0);
        Assert.Equal([200, 409], responses.Select(r => (int)r.StatusCode).Order().ToArray());
        // A transacao perdedora nao deixa mensagem orfa: exatamente uma linha de outbox.
        Assert.Equal("orcamento.aprovado.v1", Assert.Single(await Outbox(factory)).Tipo);
        Assert.Equal(1, await DispatchAsync(factory)); Assert.Single(sqs.Delivered("main"));
    }

    // --- pagamento.resultado.v1 (envelope completo)

    [Fact]
    public async Task PaymentResultUsesFullEnvelopeWithStableEventId()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, checkoutStarted: true); var paymentId = budget.Pagamento!.Id;
        provider.WebhookResult = (paymentId, StatusPagamento.Capturado, "mp-payment-1");
        using var anonymous = factory.CreateClient();
        // Primeira entrega: SQS principal e DLQ fora do ar -> 503, alerta, resultado ainda nao publicado.
        sqs.Fails = (_, _) => true;
        var failed = await SendWebhook(anonymous);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("resultado_nao_publicado", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(["billing.sqs.dlq_failed", "billing.payment_sync_failed"], alerts.Codes);
        Assert.Equal(5, sqs.AttemptsTo("main"));
        // Reentrega com o SQS de volta: republica com o mesmo eventId da tentativa anterior.
        sqs.Fails = (_, _) => false;
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(anonymous)).StatusCode);
        var body = Assert.Single(sqs.Delivered("main"));
        var envelope = Parse(body);
        Assert.Equal(["eventId", "tipo", "ordemServicoId", "ocorreuEm", "pagamentoId", "status", "referencia"], Names(envelope));
        Assert.Equal("pagamento.resultado.v1", envelope.GetProperty("tipo").GetString());
        Assert.Equal(budget.OrdemServicoId, envelope.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(paymentId, envelope.GetProperty("pagamentoId").GetGuid());
        Assert.Equal("Capturado", envelope.GetProperty("status").GetString());
        Assert.Equal("mp-payment-1", envelope.GetProperty("referencia").GetString());
        AssertUtcRecent(envelope);
        var eventId = envelope.GetProperty("eventId").GetGuid();
        Assert.NotEqual(Guid.Empty, eventId);
        Assert.All(sqs.Attempted("main"), attempt => Assert.Equal(eventId, Parse(attempt).GetProperty("eventId").GetGuid()));
        // Redelivery apos publicado: nada novo; o resultado foi marcado como publicado.
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(anonymous)).StatusCode); Assert.Single(sqs.Delivered("main"));
        Assert.True((await Db(factory, db => db.Pagamentos.AsNoTracking().SingleAsync(p => p.Id == paymentId))).ResultadoPublicado);
        // Outro pagamento e outro status: outro eventId e a ordem de servico do respectivo orcamento.
        var other = await SeedBudget(factory, checkoutStarted: true);
        provider.WebhookResult = (other.Pagamento!.Id, StatusPagamento.Rejeitado, "mp-payment-2");
        Assert.Equal(HttpStatusCode.Accepted, (await SendWebhook(anonymous, requestId: "request-2")).StatusCode);
        var rejected = Parse(sqs.Delivered("main")[1]);
        Assert.Equal("Rejeitado", rejected.GetProperty("status").GetString()); Assert.Equal("mp-payment-2", rejected.GetProperty("referencia").GetString());
        Assert.Equal(other.OrdemServicoId, rejected.GetProperty("ordemServicoId").GetGuid()); Assert.NotEqual(eventId, rejected.GetProperty("eventId").GetGuid());
        Assert.Equal(0, provider.RefundCalls);
    }

    // --- billing.orcamento.cancelar.v1

    [Fact]
    public async Task CancelCommandCancelsPendingAndApprovedWithoutPayment()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var pending = await SeedBudget(factory); var approved = await SeedBudget(factory, approved: true);
        var correlacao = Guid.NewGuid(); var event1 = Guid.NewGuid(); var event2 = Guid.NewGuid();

        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, CancelCommand(event1, pending, correlacao)));
        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, CancelCommand(event2, approved)));

        var persisted = await Db(factory, async db => (await db.Orcamentos.AsNoTracking().Include(o => o.Pagamento).ToListAsync()).ToDictionary(o => o.Id));
        Assert.Equal(StatusOrcamento.Cancelado, persisted[pending.Id].Status); Assert.Null(persisted[pending.Id].Pagamento);
        Assert.Equal(StatusOrcamento.Cancelado, persisted[approved.Id].Status);
        Assert.Equal(StatusPagamento.SemTentativa, persisted[approved.Id].Pagamento!.Status);
        var rows = await Outbox(factory); Assert.Equal(2, rows.Length);
        var first = Parse(rows.Single(r => Parse(r.Payload).GetProperty("orcamentoId").GetGuid() == pending.Id).Payload);
        Assert.Equal(["eventId", "tipo", "correlacaoId", "ordemServicoId", "ocorreuEm", "orcamentoId"], Names(first));
        Assert.Equal("orcamento.cancelado.v1", first.GetProperty("tipo").GetString());
        Assert.Equal(correlacao, first.GetProperty("correlacaoId").GetGuid());
        Assert.Equal(pending.OrdemServicoId, first.GetProperty("ordemServicoId").GetGuid());
        Assert.NotEqual(Guid.Empty, first.GetProperty("eventId").GetGuid()); Assert.NotEqual(event1, first.GetProperty("eventId").GetGuid());
        AssertUtcRecent(first);
        var second = Parse(rows.Single(r => Parse(r.Payload).GetProperty("orcamentoId").GetGuid() == approved.Id).Payload);
        Assert.Equal(["eventId", "tipo", "ordemServicoId", "ocorreuEm", "orcamentoId"], Names(second)); // sem correlacaoId quando o comando nao trouxe

        // As respostas saem pela fila de entrada da Saga via outbox.
        Assert.Equal(2, await DispatchAsync(factory));
        Assert.Equal(rows.Select(r => r.Payload).Order().ToArray(), sqs.Delivered("main").Order().ToArray());
        Assert.Empty(sqs.Delivered("dlq")); Assert.Empty(alerts.Codes);
        // Orcamento cancelado nao abre checkout: o pagamento continua sem tentativa e nada vai ao provedor.
        using var admin = Authorized(factory.CreateClient());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/pagamentos/{approved.Pagamento!.Id}/checkout", null)).StatusCode);
        Assert.Equal(0, provider.Checkouts); Assert.Equal(0, provider.RefundCalls);
        // Nao se aprova um orcamento cancelado.
        using var cliente = Authorized(factory.CreateClient(), "Cliente", pending.ClienteId);
        Assert.Equal(HttpStatusCode.Conflict, (await cliente.PostAsync($"/api/v1/orcamentos/{pending.Id}/aprovacao", null)).StatusCode);
    }

    [Fact]
    public async Task CancelCommandRefusesWhenPaymentExistsAndNeverRefunds()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var paying = await SeedBudget(factory, checkoutStarted: true);
        var captured = await SeedBudget(factory, checkoutStarted: true);
        await Db(factory, async db => { var p = await db.Pagamentos.SingleAsync(x => x.Id == captured.Pagamento!.Id); p.AplicarEstado(StatusPagamento.Capturado, "mp-1"); await db.SaveChangesAsync(); return 0; });
        var correlacao = Guid.NewGuid();

        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, CancelCommand(Guid.NewGuid(), paying, correlacao)));
        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, CancelCommand(Guid.NewGuid(), captured)));

        var persisted = await Db(factory, async db => (await db.Orcamentos.AsNoTracking().Include(o => o.Pagamento).ToListAsync()).ToDictionary(o => o.Id));
        Assert.Equal(StatusOrcamento.Aprovado, persisted[paying.Id].Status); Assert.Equal(StatusPagamento.Pendente, persisted[paying.Id].Pagamento!.Status);
        Assert.Equal(StatusOrcamento.Aprovado, persisted[captured.Id].Status); Assert.Equal(StatusPagamento.Capturado, persisted[captured.Id].Pagamento!.Status);
        var rows = await Outbox(factory); Assert.Equal(2, rows.Length);
        var refusal = Parse(rows.Single(r => Parse(r.Payload).GetProperty("orcamentoId").GetGuid() == paying.Id).Payload);
        Assert.Equal(["eventId", "tipo", "correlacaoId", "ordemServicoId", "ocorreuEm", "orcamentoId", "motivo"], Names(refusal));
        Assert.Equal("orcamento.cancelamento-recusado.v1", refusal.GetProperty("tipo").GetString());
        Assert.Equal(correlacao, refusal.GetProperty("correlacaoId").GetGuid());
        Assert.Equal(paying.OrdemServicoId, refusal.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal("pagamento_existente", refusal.GetProperty("motivo").GetString());
        Assert.All(rows, r => Assert.Equal("orcamento.cancelamento-recusado.v1", r.Tipo));
        Assert.Equal(2, await DispatchAsync(factory)); Assert.Equal(2, sqs.Delivered("main").Length);
        // Recusa de negocio e resposta normal: sem alerta tecnico, sem DLQ e, principalmente, sem estorno nem novo checkout.
        Assert.Empty(alerts.Codes); Assert.Empty(sqs.Delivered("cmd-dlq"));
        Assert.Equal(0, provider.RefundCalls); Assert.Equal(0, provider.Checkouts);
        Assert.Empty(await Db(factory, db => db.Reembolsos.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task DuplicateEventIdNeverRepeatsEffectOrResponse()
    {
        var (factory, postgres, gate, _, sqs, _, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true); var eventId = Guid.NewGuid(); var command = CancelCommand(eventId, budget, Guid.NewGuid());
        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, command));
        Assert.Equal(DesfechoComando.Duplicado, await HandleCommandAsync(factory, command));
        Assert.Equal(DesfechoComando.Duplicado, await HandleCommandAsync(factory, command));
        Assert.Single(await Outbox(factory));
        Assert.Equal(eventId, Assert.Single(await Db(factory, db => db.EventosProcessados.AsNoTracking().ToListAsync())).Id);
        // Mesmo eventId depois de recusa tambem e duplicata: nao ha segunda resposta.
        var paying = await SeedBudget(factory, checkoutStarted: true); var refusedEvent = CancelCommand(Guid.NewGuid(), paying);
        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, refusedEvent));
        Assert.Equal(DesfechoComando.Duplicado, await HandleCommandAsync(factory, refusedEvent));
        Assert.Equal(2, (await Outbox(factory)).Length);

        // Duplicata concorrente: as duas chegam a gravar ao mesmo tempo; so uma vence, a outra reprocessa e ve a duplicata.
        var racing = await SeedBudget(factory); var racingCommand = CancelCommand(Guid.NewGuid(), racing);
        gate.Arm(2);
        var outcomes = await Task.WhenAll(HandleCommandAsync(factory, racingCommand), HandleCommandAsync(factory, racingCommand));
        gate.Arm(0);
        Assert.Equal([DesfechoComando.Cancelado, DesfechoComando.Duplicado], outcomes.Order().ToArray());
        var rows = await Outbox(factory);
        Assert.Equal(3, rows.Length); Assert.Single(rows, r => Parse(r.Payload).GetProperty("orcamentoId").GetGuid() == racing.Id);
        Assert.Equal(3, (await Db(factory, db => db.EventosProcessados.CountAsync())));
        Assert.Equal(StatusOrcamento.Cancelado, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == racing.Id))).Status);
        Assert.Equal(3, await DispatchAsync(factory)); Assert.Equal(3, sqs.Delivered("main").Length);
        Assert.Equal(0, provider.RefundCalls);
    }

    [Fact]
    public async Task CancelRacingAgainstCheckoutNeverLeavesCancelledBudgetWithActiveCharge()
    {
        var (factory, postgres, gate, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true);
        using var admin = Authorized(factory.CreateClient());
        gate.Arm(2);
        var checkout = admin.PostAsync($"/api/v1/pagamentos/{budget.Pagamento!.Id}/checkout", null);
        var cancel = HandleCommandAsync(factory, CancelCommand(Guid.NewGuid(), budget));
        await Task.WhenAll(checkout, cancel);
        gate.Arm(0);
        var state = await Db(factory, async db => await db.Orcamentos.AsNoTracking().Include(o => o.Pagamento).SingleAsync(o => o.Id == budget.Id));
        var row = Assert.Single(await Outbox(factory));
        if (state.Status == StatusOrcamento.Cancelado)
        {
            Assert.Equal(StatusPagamento.SemTentativa, state.Pagamento!.Status); Assert.Equal(HttpStatusCode.Conflict, (await checkout).StatusCode);
            Assert.Equal("orcamento.cancelado.v1", row.Tipo); Assert.Equal(DesfechoComando.Cancelado, await cancel);
        }
        else
        {
            Assert.Equal(StatusOrcamento.Aprovado, state.Status); Assert.Equal(StatusPagamento.Pendente, state.Pagamento!.Status); Assert.Equal(HttpStatusCode.OK, (await checkout).StatusCode);
            Assert.Equal("orcamento.cancelamento-recusado.v1", row.Tipo); Assert.Equal(DesfechoComando.Recusado, await cancel);
        }
        Assert.Equal(0, provider.RefundCalls); Assert.Empty(alerts.Codes); Assert.Empty(sqs.Delivered("cmd-dlq"));
    }

    [Fact]
    public async Task MalformedUnknownAndMissingBudgetCommandsAlertAndAnswerOrDeadLetter()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true);

        // Sem identificadores validos: alerta e DLQ de comandos, sem resposta (nao ha para quem responder).
        Assert.Equal(DesfechoComando.DeadLetter, await HandleCommandAsync(factory, "isto nao e json"));
        Assert.Equal(DesfechoComando.DeadLetter, await HandleCommandAsync(factory, "[1,2]"));
        var noIds = JsonSerializer.Serialize(new { eventId = Guid.NewGuid(), tipo = "billing.orcamento.cancelar.v1", orcamentoId = "nao-e-guid", ordemServicoId = budget.OrdemServicoId });
        Assert.Equal(DesfechoComando.DeadLetter, await HandleCommandAsync(factory, noIds));
        var noOrder = JsonSerializer.Serialize(new { eventId = Guid.NewGuid(), tipo = "billing.orcamento.cancelar.v1", orcamentoId = budget.Id });
        Assert.Equal(DesfechoComando.DeadLetter, await HandleCommandAsync(factory, noOrder));
        Assert.Equal(["isto nao e json", "[1,2]", noIds, noOrder], sqs.Delivered("cmd-dlq"));
        Assert.Equal(["billing.comando.malformado", "billing.comando.malformado", "billing.comando.malformado", "billing.comando.malformado"], alerts.Codes);
        Assert.Empty(await Outbox(factory));

        // Tipo desconhecido com ids validos: alerta e recusa; o orcamento nao muda.
        var unknown = CancelCommand(Guid.NewGuid(), budget, tipo: "billing.orcamento.apagar.v1");
        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, unknown));
        // Orcamento inexistente: alerta e recusa para o ordemServicoId informado.
        var ghost = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), 10m, DateTimeOffset.UtcNow);
        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, CancelCommand(Guid.NewGuid(), ghost)));
        // Ordem de servico diferente da do orcamento.
        var wrongOrder = JsonSerializer.Serialize(new { eventId = Guid.NewGuid(), tipo = "billing.orcamento.cancelar.v1", orcamentoId = budget.Id, ordemServicoId = Guid.NewGuid() });
        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, wrongOrder));
        // Sem eventId, com ids validos: recusa e a reentrega do mesmo corpo e duplicata (id derivado do corpo).
        var noEvent = JsonSerializer.Serialize(new { tipo = "billing.orcamento.cancelar.v1", orcamentoId = budget.Id, ordemServicoId = budget.OrdemServicoId });
        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, noEvent));
        Assert.Equal(DesfechoComando.Duplicado, await HandleCommandAsync(factory, noEvent));

        var rows = await Outbox(factory);
        Assert.Equal(["tipo_desconhecido", "orcamento_inexistente", "ordem_divergente", "mensagem_malformada"], rows.Select(r => Parse(r.Payload).GetProperty("motivo").GetString()!).ToArray());
        Assert.All(rows, r => Assert.Equal("orcamento.cancelamento-recusado.v1", r.Tipo));
        Assert.Equal(ghost.OrdemServicoId, Parse(rows[1].Payload).GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(ghost.Id, Parse(rows[1].Payload).GetProperty("orcamentoId").GetGuid());
        Assert.Equal(["billing.comando.malformado", "billing.comando.malformado", "billing.comando.malformado", "billing.comando.malformado",
            "billing.comando.tipo_desconhecido", "billing.comando.orcamento_inexistente", "billing.comando.ordem_divergente", "billing.comando.malformado"], alerts.Codes);
        Assert.Equal(4, sqs.Delivered("cmd-dlq").Length);
        Assert.Equal(StatusOrcamento.Aprovado, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == budget.Id))).Status);
        Assert.Equal(0, provider.RefundCalls); Assert.Equal(0, provider.Checkouts);
    }

    [Fact]
    public async Task CommandProcessingFailureRetriesThenDeadLettersWithAlert()
    {
        var (factory, postgres, _, fail, sqs, alerts, provider, delays) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true); var command = CancelCommand(Guid.NewGuid(), budget);
        fail.Fail(int.MaxValue);
        Assert.Equal(DesfechoComando.DeadLetter, await HandleCommandAsync(factory, command));
        Assert.Equal(5, fail.Attempts); Assert.Equal([1, 2, 3, 4], delays); // 5 tentativas, backoff entre elas
        Assert.Equal(["billing.comando.esgotado"], alerts.Codes);
        Assert.Equal([command], sqs.Delivered("cmd-dlq"));
        fail.Fail(0);
        Assert.Equal(StatusOrcamento.Aprovado, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == budget.Id))).Status);
        Assert.Empty(await Outbox(factory)); Assert.Empty(await Db(factory, db => db.EventosProcessados.ToListAsync()));
        // Falha transitoria que se recupera dentro das 5 tentativas: processa uma vez, sem DLQ nem novo alerta.
        var again = await SeedBudget(factory, approved: true);
        fail.Fail(2);
        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, CancelCommand(Guid.NewGuid(), again)));
        Assert.Equal(7, fail.Attempts); Assert.Equal([1, 2, 3, 4, 1, 2], delays);
        Assert.Single(await Outbox(factory)); Assert.Single(alerts.Codes); Assert.Single(sqs.Delivered("cmd-dlq"));
        Assert.Equal(0, provider.RefundCalls);
    }

    [Fact]
    public async Task ConsumerDeletesResolvedMessagesAndKeepsUnresolvedOnes()
    {
        var (factory, postgres, _, _, sqs, alerts, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true); var command = CancelCommand(Guid.NewGuid(), budget);
        sqs.Enqueue("cmd", command, "r1"); sqs.Enqueue("cmd", command, "r2"); sqs.Enqueue("cmd", "lixo", "r3");
        var consumer = new ComandosConsumer(factory.Services.GetRequiredService<ProcessadorComandos>(), sqs, factory.Services.GetRequiredService<IConfiguration>(), NullLogger<ComandosConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        await WaitUntil(() => sqs.Deleted.Length == 3, "tres mensagens apagadas");
        await consumer.StopAsync(CancellationToken.None);
        Assert.Equal(["r1", "r2", "r3"], sqs.Deleted.Order().ToArray());
        Assert.Equal(StatusOrcamento.Cancelado, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == budget.Id))).Status);
        Assert.Single(await Outbox(factory)); Assert.Equal(["lixo"], sqs.Delivered("cmd-dlq"));
        Assert.Equal(["billing.comando.malformado"], alerts.Codes);

        // Nem a DLQ de comandos aceita a mensagem: alerta e a mensagem NAO e apagada (volta a fila).
        sqs.Fails = (url, _) => url == "cmd-dlq";
        await consumer.TratarAsync("cmd", new Message { Body = "lixo 2", ReceiptHandle = "r4", MessageId = "r4" }, CancellationToken.None);
        Assert.DoesNotContain("r4", sqs.Deleted); Assert.Equal("billing.comando.dlq_failed", alerts.Codes[^1]);
    }

    // --- Despachador do outbox: retry, DLQ, alerta e retomada

    private static async Task<Guid> InsertPending(MensageriaFactory factory, string tipo = "orcamento.aprovado.v1", DateTimeOffset? at = null)
    {
        var id = Guid.NewGuid(); var payload = JsonSerializer.Serialize(new { eventId = id, tipo });
        await Db(factory, async db => { db.MensagensSaida.Add(new MensagemSaida(id, tipo, payload, at ?? DateTimeOffset.UtcNow)); await db.SaveChangesAsync(); return 0; });
        return id;
    }

    [Fact]
    public async Task DispatcherRetriesWithBackoffThenRecoversWithoutAlert()
    {
        var (factory, postgres, _, _, sqs, alerts, _, delays) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var id = await InsertPending(factory);
        sqs.Fails = (url, n) => url == "main" && n <= 2;
        Assert.Equal(1, await DispatchAsync(factory));
        Assert.Equal(3, sqs.AttemptsTo("main")); Assert.Equal([1, 2], delays); Assert.Equal(0, sqs.AttemptsTo("dlq")); Assert.Empty(alerts.Codes);
        var row = Assert.Single(await Outbox(factory));
        Assert.Equal(id, row.Id); Assert.NotNull(row.EnviadoEm); Assert.Null(row.EsgotadaEm); Assert.Equal(3, row.Tentativas);
    }

    [Fact]
    public async Task DispatcherExhaustsFiveAttemptsThenDeadLettersWithAlertAndKeepsOrder()
    {
        var (factory, postgres, _, _, sqs, alerts, _, delays) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var first = await InsertPending(factory, at: DateTimeOffset.UtcNow.AddSeconds(-10)); var second = await InsertPending(factory, at: DateTimeOffset.UtcNow.AddSeconds(-5));
        sqs.Fails = (url, _) => url == "main";
        Assert.Equal(2, await DispatchAsync(factory));
        Assert.Equal(10, sqs.AttemptsTo("main")); Assert.Equal(2, sqs.AttemptsTo("dlq"));
        Assert.Equal([1, 2, 3, 4, 1, 2, 3, 4], delays);
        Assert.Equal(["billing.sqs.exhausted", "billing.sqs.exhausted"], alerts.Codes);
        Assert.Equal([first, second], sqs.Delivered("dlq").Select(b => Parse(b).GetProperty("eventId").GetGuid()).ToArray());
        var rows = await Outbox(factory);
        Assert.All(rows, r => { Assert.Null(r.EnviadoEm); Assert.NotNull(r.EsgotadaEm); Assert.Equal(5, r.Tentativas); });
        // Esgotada e encaminhada a DLQ: nao e reenviada nos ciclos seguintes.
        sqs.Fails = (_, _) => false;
        Assert.Equal(0, await DispatchAsync(factory)); Assert.Equal(10, sqs.AttemptsTo("main"));
    }

    [Fact]
    public async Task DispatcherKeepsMessagePendingWhenDeadLetterQueueAlsoFailsAndResumesLater()
    {
        var (factory, postgres, _, _, sqs, alerts, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var id = await InsertPending(factory);
        sqs.Fails = (_, _) => true;
        Assert.Equal(0, await DispatchAsync(factory));
        Assert.Equal(["billing.sqs.dlq_failed"], alerts.Codes);
        var pending = Assert.Single(await Outbox(factory));
        Assert.Null(pending.EnviadoEm); Assert.Null(pending.EsgotadaEm);
        sqs.Fails = (_, _) => false;
        Assert.Equal(1, await DispatchAsync(factory));
        Assert.Equal(id, Parse(Assert.Single(sqs.Delivered("main"))).GetProperty("eventId").GetGuid());
        Assert.NotNull(Assert.Single(await Outbox(factory)).EnviadoEm);
    }

    [Fact]
    public async Task DispatcherBackgroundServiceResumesPendingMessagesOnStartup()
    {
        var (factory, postgres, _, _, sqs, alerts, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        // Mensagens deixadas por um processo anterior (uma ja enviada, duas pendentes).
        var sentId = await InsertPending(factory, at: DateTimeOffset.UtcNow.AddMinutes(-3));
        await Db(factory, db => db.MensagensSaida.Where(m => m.Id == sentId).ExecuteUpdateAsync(s => s.SetProperty(m => m.EnviadoEm, DateTimeOffset.UtcNow)));
        var a = await InsertPending(factory, at: DateTimeOffset.UtcNow.AddMinutes(-2)); var b = await InsertPending(factory, at: DateTimeOffset.UtcNow.AddMinutes(-1));
        var dispatcher = new OutboxDispatcher(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<IConfiguration>(), NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntil(() => sqs.Delivered("main").Length == 2, "duas mensagens pendentes publicadas");
        // Mensagem criada depois do inicio tambem sai, no ciclo seguinte.
        var c = await InsertPending(factory);
        await WaitUntil(() => sqs.Delivered("main").Length == 3, "mensagem nova publicada");
        await dispatcher.StopAsync(CancellationToken.None);
        Assert.Equal([a, b, c], sqs.Delivered("main").Select(body => Parse(body).GetProperty("eventId").GetGuid()).ToArray());
        Assert.All(await Outbox(factory), r => Assert.NotNull(r.EnviadoEm)); Assert.Empty(alerts.Codes);
    }

    // --- C4 a C6 de eventos-portugues: comando cobranca.cancelar-orcamento.v1, alias legado e tipo desconhecido

    [Fact]
    public async Task PortugueseCancellationAppliesAndRecordsInbox()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true);
        var eventId = Guid.NewGuid();
        var correlacao = Guid.NewGuid();
        var command = CancelCommand(eventId, budget, correlacao, tipo: "cobranca.cancelar-orcamento.v1");

        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, command));

        var persisted = await Db(factory, db => db.Orcamentos.AsNoTracking().Include(o => o.Pagamento).SingleAsync(o => o.Id == budget.Id));
        Assert.Equal(StatusOrcamento.Cancelado, persisted.Status);
        Assert.Equal(StatusPagamento.SemTentativa, persisted.Pagamento!.Status);

        var inbox = Assert.Single(await Db(factory, db => db.EventosProcessados.AsNoTracking().ToListAsync()));
        Assert.Equal(eventId, inbox.Id);
        Assert.Equal("cobranca.cancelar-orcamento.v1", inbox.Tipo);

        var row = Assert.Single(await Outbox(factory));
        Assert.Equal("orcamento.cancelado.v1", row.Tipo);
        var envelope = Parse(row.Payload);
        Assert.Equal(["eventId", "tipo", "correlacaoId", "ordemServicoId", "ocorreuEm", "orcamentoId"], Names(envelope));
        Assert.Equal("orcamento.cancelado.v1", envelope.GetProperty("tipo").GetString());
        Assert.Equal(correlacao, envelope.GetProperty("correlacaoId").GetGuid());
        Assert.Equal(budget.OrdemServicoId, envelope.GetProperty("ordemServicoId").GetGuid());
        Assert.Equal(budget.Id, envelope.GetProperty("orcamentoId").GetGuid());
        Assert.Empty(alerts.Codes);
        Assert.Empty(sqs.Delivered("cmd-dlq"));
        Assert.Equal(0, provider.RefundCalls);
    }

    [Fact]
    public async Task LegacyAndPortugueseAliasesDeduplicateByEventId()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;

        // Caso 1: comando legado chega primeiro, comando em português com o mesmo eventId chega depois.
        var budget1 = await SeedBudget(factory, approved: true);
        var event1 = Guid.NewGuid();
        var correlacao1 = Guid.NewGuid();
        var legacyFirst = CancelCommand(event1, budget1, correlacao1, tipo: "billing.orcamento.cancelar.v1");
        var portugueseSecond = CancelCommand(event1, budget1, correlacao1, tipo: "cobranca.cancelar-orcamento.v1");

        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, legacyFirst));
        Assert.Equal(DesfechoComando.Duplicado, await HandleCommandAsync(factory, portugueseSecond));

        // Caso 2: comando em português chega primeiro, comando legado com o mesmo eventId chega depois.
        var budget2 = await SeedBudget(factory, approved: true);
        var event2 = Guid.NewGuid();
        var correlacao2 = Guid.NewGuid();
        var portugueseFirst = CancelCommand(event2, budget2, correlacao2, tipo: "cobranca.cancelar-orcamento.v1");
        var legacySecond = CancelCommand(event2, budget2, correlacao2, tipo: "billing.orcamento.cancelar.v1");

        Assert.Equal(DesfechoComando.Cancelado, await HandleCommandAsync(factory, portugueseFirst));
        Assert.Equal(DesfechoComando.Duplicado, await HandleCommandAsync(factory, legacySecond));

        var budgets = await Db(factory, async db => (await db.Orcamentos.AsNoTracking().ToListAsync()).ToDictionary(o => o.Id));
        Assert.Equal(StatusOrcamento.Cancelado, budgets[budget1.Id].Status);
        Assert.Equal(StatusOrcamento.Cancelado, budgets[budget2.Id].Status);

        var inbox = await Db(factory, async db => (await db.EventosProcessados.AsNoTracking().ToListAsync()).ToDictionary(e => e.Id));
        Assert.Equal(2, inbox.Count);
        Assert.Equal("billing.orcamento.cancelar.v1", inbox[event1].Tipo);
        Assert.Equal("cobranca.cancelar-orcamento.v1", inbox[event2].Tipo);

        var rows = await Outbox(factory);
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, r => Parse(r.Payload).GetProperty("orcamentoId").GetGuid() == budget1.Id);
        Assert.Single(rows, r => Parse(r.Payload).GetProperty("orcamentoId").GetGuid() == budget2.Id);
        Assert.Empty(alerts.Codes);
        Assert.Empty(sqs.Delivered("cmd-dlq"));
        Assert.Equal(0, provider.RefundCalls);
    }

    [Fact]
    public async Task UnknownCancellationTypeGoesToDeadLetter()
    {
        var (factory, postgres, _, _, sqs, alerts, provider, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedBudget(factory, approved: true);
        var eventId = Guid.NewGuid();
        var unknown = CancelCommand(eventId, budget, tipo: "billing.orcamento.apagar.v1");

        Assert.Equal(DesfechoComando.Recusado, await HandleCommandAsync(factory, unknown));
        var row = Assert.Single(await Outbox(factory));
        Assert.Equal("orcamento.cancelamento-recusado.v1", row.Tipo);
        Assert.Equal("tipo_desconhecido", Parse(row.Payload).GetProperty("motivo").GetString());
        Assert.Equal(["billing.comando.tipo_desconhecido"], alerts.Codes);
        Assert.Equal(StatusOrcamento.Aprovado, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == budget.Id))).Status);
        Assert.Equal(0, provider.RefundCalls);

        var unknownWithoutIds = JsonSerializer.Serialize(new { eventId = Guid.NewGuid(), tipo = "billing.orcamento.apagar.v1" });
        Assert.Equal(DesfechoComando.DeadLetter, await HandleCommandAsync(factory, unknownWithoutIds));
        Assert.Equal([unknownWithoutIds], sqs.Delivered("cmd-dlq"));
        Assert.Equal(["billing.comando.tipo_desconhecido", "billing.comando.malformado"], alerts.Codes);
    }
}
