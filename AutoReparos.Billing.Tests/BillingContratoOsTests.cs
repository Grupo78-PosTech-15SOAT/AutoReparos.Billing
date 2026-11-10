using AutoReparos.Billing.Application;
using AutoReparos.Billing.Domain;
using AutoReparos.Billing.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AutoReparos.Billing.Tests;

// Teste de contrato com os literais de "Payloads de referência" do contracts.md da Saga (fonte única entre OS, Billing e Execution).
// Usa o cliente e o publicador reais com transporte falso, para que um fake de teste não esconda divergência de contrato.
public sealed partial class BillingLifecycleTests
{
    private const string OsGetReferencia = """{ "id": "11111111-1111-1111-1111-111111111111", "clienteId": "22222222-2222-2222-2222-222222222222", "veiculoId": "33333333-3333-3333-3333-333333333333", "observacao": null, "status": "Concluida", "historico": [] }""";

    private const string PagamentoResultadoReferencia = """{ "eventId": "44444444-4444-4444-4444-444444444444", "tipo": "pagamento.resultado.v1", "ordemServicoId": "11111111-1111-1111-1111-111111111111", "ocorreuEm": "2026-11-10T12:00:00Z", "pagamentoId": "55555555-5555-5555-5555-555555555555", "status": "Capturado", "referencia": "mp-1" }""";

    private static readonly Guid OsReferencia = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PagamentoReferencia = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task OsContractReference_ConcludedStatusReleasesChargeAndOtherStatusesDoNot()
    {
        var alerts = new Alerts();
        var concluida = new FakeHandler(_ => Json(HttpStatusCode.OK, OsGetReferencia));
        Assert.True(await OsReadiness(concluida, alerts).ServicoConcluidoAsync(OsReferencia, CancellationToken.None));

        foreach (var status in new[] { "Recebida", "EmExecucao" })
        {
            var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, OsGetReferencia.Replace("\"status\": \"Concluida\"", $"\"status\": \"{status}\"")));
            Assert.False(await OsReadiness(handler, alerts).ServicoConcluidoAsync(OsReferencia, CancellationToken.None));
        }
        Assert.Equal(0, alerts.Count);
    }

    [Fact]
    public async Task OsContractReference_ConcludedOsGetEnablesCheckoutThroughRealReadinessClient()
    {
        var (store, _, payment) = SeedPayment(); var provider = new Provider(); var alerts = new Alerts();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, OsGetReferencia));
        using (var factory = new BillingApiFactory(store, provider, new Publisher(), alerts, OsReadiness(handler, alerts)))
        {
            using var client = Authorized(factory.CreateClient());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/pagamentos/{payment.Id}/checkout", null)).StatusCode);
        }
        Assert.Equal(1, provider.Checkouts);

        var (store2, _, payment2) = SeedPayment(); var provider2 = new Provider();
        var emExecucao = new FakeHandler(_ => Json(HttpStatusCode.OK, OsGetReferencia.Replace("\"Concluida\"", "\"EmExecucao\"")));
        using (var factory = new BillingApiFactory(store2, provider2, new Publisher(), alerts, OsReadiness(emExecucao, alerts)))
        {
            using var client = Authorized(factory.CreateClient());
            Assert.NotEqual(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/pagamentos/{payment2.Id}/checkout", null)).StatusCode);
        }
        Assert.Equal(0, provider2.Checkouts);
    }

    [Fact]
    public async Task OsContractReference_PublisherEmitsExactlyThePaymentResultReference()
    {
        var ocorreuEm = DateTime.Parse("2026-11-10T12:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var publicados = new List<string>();
        var sqs = new FakeSqs();
        var publisher = new SqsResultPublisher(sqs,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sqs:QueueUrl"] = "main", ["Sqs:DeadLetterQueueUrl"] = "dlq" }).Build(),
            NullLogger<SqsResultPublisher>.Instance, new Alerts(), _ => TimeSpan.Zero);

        // Evento montado como o serviço o monta (eventId determinístico) e evento com o eventId literal da referência.
        var doServico = MensagensSaga.ResultadoPagamento(OsReferencia, PagamentoReferencia, StatusPagamento.Capturado, "mp-1", ocorreuEm);
        await publisher.PublicarAsync(doServico, CancellationToken.None);
        await publisher.PublicarAsync(doServico with { EventId = Guid.Parse("44444444-4444-4444-4444-444444444444") }, CancellationToken.None);

        publicados.AddRange(sqs.Delivered("main"));
        Assert.Equal(2, publicados.Count);
        Assert.NotEqual(Guid.Empty, doServico.EventId);
        Assert.Equal(MensagensSaga.ResultadoPagamento(OsReferencia, PagamentoReferencia, StatusPagamento.Capturado, "mp-1", ocorreuEm).EventId, doServico.EventId);

        var esperado = CamposOrdenados(PagamentoResultadoReferencia);
        Assert.Equal(esperado, CamposOrdenados(publicados[1]));
        var servico = CamposOrdenados(publicados[0]);
        Assert.Equal(esperado.Select(c => c.Nome), servico.Select(c => c.Nome));
        Assert.Equal(esperado.Where(c => c.Nome != "eventId"), servico.Where(c => c.Nome != "eventId"));
        Assert.Equal("2026-11-10T12:00:00Z", JsonDocument.Parse(publicados[0]).RootElement.GetProperty("ocorreuEm").GetString());
    }

    private static (string Nome, string Valor)[] CamposOrdenados(string json) =>
        [.. JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetRawText()))];
}
