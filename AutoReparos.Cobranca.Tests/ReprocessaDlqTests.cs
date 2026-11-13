using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Cobranca.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoReparos.Cobranca.Tests;

/// <summary>C12 de saga-compensacoes na Cobrança: <c>POST /api/v1/admin/dlq/reprocessar</c> devolve as mensagens das DLQs às filas de origem.</summary>
public sealed class ReprocessaDlqTests
{
    private const string DlqComandos = "dlq-comandos", FilaComandos = "fila-comandos", DlqEventos = "dlq-eventos", FilaEventos = "fila-eventos";

    private sealed class SqsFalso() : AmazonSQSClient(new AnonymousAWSCredentials(), new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.USEast1 })
    {
        public Dictionary<string, Queue<Message>> Dlqs { get; } = new() { [DlqComandos] = new(), [DlqEventos] = new() };
        public List<(string Fila, string Corpo)> Enviadas { get; } = [];
        public List<string> Apagadas { get; } = [];

        public override Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request, CancellationToken cancellationToken = default)
        {
            var lote = new List<Message>();
            if (Dlqs.TryGetValue(request.QueueUrl, out var fila)) while (fila.Count > 0 && lote.Count < request.MaxNumberOfMessages) lote.Add(fila.Dequeue());
            return Task.FromResult(new ReceiveMessageResponse { Messages = lote });
        }

        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add((request.QueueUrl, request.MessageBody));
            return Task.FromResult(new SendMessageResponse());
        }

        public override Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default)
        {
            Apagadas.Add(request.ReceiptHandle);
            return Task.FromResult(new DeleteMessageResponse());
        }
    }

    private sealed class Alertas : IAlertSink
    {
        public List<(string Codigo, string Detalhe)> Itens { get; } = [];
        public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { Itens.Add((codigo, detalhe)); return Task.CompletedTask; }
    }

    private sealed class AutenticacaoDeTeste(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var papel = Request.Headers["X-Test-Role"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(papel)) return Task.FromResult(AuthenticateResult.NoResult());
            var identidade = new ClaimsIdentity([new Claim(ClaimTypes.Role, papel), new Claim(ClaimTypes.NameIdentifier, "admin-1")], Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), Scheme.Name)));
        }
    }

    private sealed class Fabrica(SqsFalso sqs, Alertas alertas) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:1",
                ["Sqs:ComandosDeadLetterQueueUrl"] = DlqComandos,
                ["Sqs:ComandosQueueUrl"] = FilaComandos,
                ["Sqs:DeadLetterQueueUrl"] = DlqEventos,
                ["Sqs:QueueUrl"] = FilaEventos
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAmazonSQS>(); services.AddSingleton<IAmazonSQS>(sqs);
                services.RemoveAll<IAlertSink>(); services.AddSingleton<IAlertSink>(alertas);
                services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "Test"; o.DefaultChallengeScheme = "Test"; o.DefaultScheme = "Test"; })
                    .AddScheme<AuthenticationSchemeOptions, AutenticacaoDeTeste>("Test", _ => { });
            });
        }
    }

    private static Message Msg(string recibo) => new() { ReceiptHandle = recibo, Body = $"{{\"eventId\":\"{Guid.NewGuid()}\",\"recibo\":\"{recibo}\"}}" };

    private static async Task<HttpResponseMessage> PostarAsync(Fabrica fabrica, string? papel, string corpo)
    {
        using var cliente = fabrica.CreateClient();
        if (papel is not null) cliente.DefaultRequestHeaders.Add("X-Test-Role", papel);
        return await cliente.PostAsync("/api/v1/admin/dlq/reprocessar", new StringContent(corpo, Encoding.UTF8, "application/json"));
    }

    [Fact]
    public async Task ReprocessaDlqDaCobranca_DevolveCadaDlqASuaFilaDeOrigemAteAQuantidadePedida()
    {
        var sqs = new SqsFalso(); var alertas = new Alertas();
        foreach (var r in new[] { "c1", "c2" }) sqs.Dlqs[DlqComandos].Enqueue(Msg(r));
        foreach (var r in new[] { "e1", "e2", "e3" }) sqs.Dlqs[DlqEventos].Enqueue(Msg(r));
        using var fabrica = new Fabrica(sqs, alertas);

        var resposta = await PostarAsync(fabrica, "Administrador", "{\"quantidade\":4}");

        Assert.Equal(HttpStatusCode.Accepted, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((4, 4), (corpo.GetProperty("solicitadas").GetInt32(), corpo.GetProperty("reprocessadas").GetInt32()));
        Assert.Equal([FilaComandos, FilaComandos, FilaEventos, FilaEventos], sqs.Enviadas.Select(e => e.Fila));
        Assert.Equal(["c1", "c2", "e1", "e2"], sqs.Apagadas);
        Assert.Single(sqs.Dlqs[DlqEventos]);
        var alerta = Assert.Single(alertas.Itens);
        Assert.Equal("billing.dlq-reprocessada", alerta.Codigo);
        Assert.Contains("admin-1", alerta.Detalhe);
    }

    [Theory]
    [InlineData(null, "{\"quantidade\":1}", HttpStatusCode.Unauthorized)]
    [InlineData("Atendente", "{\"quantidade\":1}", HttpStatusCode.Forbidden)]
    [InlineData("Administrador", "{\"quantidade\":0}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("Administrador", "{\"quantidade\":101}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("Administrador", "{}", HttpStatusCode.UnprocessableEntity)]
    public async Task ReprocessaDlqDaCobranca_RecusaPapelEQuantidadeInvalidos(string? papel, string corpo, HttpStatusCode esperado)
    {
        var sqs = new SqsFalso();
        sqs.Dlqs[DlqComandos].Enqueue(Msg("c1"));
        using var fabrica = new Fabrica(sqs, new Alertas());

        var resposta = await PostarAsync(fabrica, papel, corpo);

        Assert.Equal(esperado, resposta.StatusCode);
        Assert.Empty(sqs.Enviadas);
        Assert.Single(sqs.Dlqs[DlqComandos]);
    }
}
