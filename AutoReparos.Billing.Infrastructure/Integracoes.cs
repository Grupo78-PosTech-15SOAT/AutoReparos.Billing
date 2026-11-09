using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Billing.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutoReparos.Billing.Infrastructure;

public sealed class MercadoPagoClient(HttpClient http, IConfiguration config, IAlertSink alerts, Func<int, TimeSpan>? delay = null) : ICheckoutProvider
{
    private string Token => config["MercadoPago:AccessToken"] ?? throw new InvalidOperationException("Mercado Pago não configurado.");
    public Task<AbrirCheckout> CriarCheckoutAsync(Guid pagamentoId, decimal valor, CancellationToken ct) => RequestWithRetryAsync(
        "billing.mercadopago.checkout_failed", () =>
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadopago.com/checkout/preferences");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add("X-Idempotency-Key", pagamentoId.ToString("N"));
        request.Content = JsonContent.Create(new { external_reference = pagamentoId.ToString(), items = new[] { new { id = pagamentoId.ToString(), title = "Serviço automotivo", quantity = 1, currency_id = "BRL", unit_price = valor } } });
        return request;
    }, async (response, token) =>
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return new AbrirCheckout(body.RootElement.GetProperty("init_point").GetString()!, body.RootElement.GetProperty("id").GetString()!);
    }, ct);

    public Task<string> ReembolsarAsync(string referencia, decimal valor, string idempotencyKey, CancellationToken ct) => RequestWithRetryAsync(
        "billing.mercadopago.refund_failed", () =>
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.mercadopago.com/v1/payments/{Uri.EscapeDataString(referencia)}/refunds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        request.Headers.Add("X-Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new { amount = valor });
        return request;
    }, async (response, token) =>
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); return body.RootElement.GetProperty("id").ToString();
    }, ct);

    public Task<(Guid PagamentoId, AutoReparos.Billing.Domain.StatusPagamento Status, string Referencia)?> ConsultarPagamentoAsync(string id, CancellationToken ct) => RequestWithRetryAsync<(Guid, AutoReparos.Billing.Domain.StatusPagamento, string)?>(
        "billing.mercadopago.reconciliation_failed", () =>
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadopago.com/v1/payments/{Uri.EscapeDataString(id)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }, async (response, token) =>
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = body.RootElement;
        if (!Guid.TryParse(root.GetProperty("external_reference").GetString(), out var paymentId)) return null;
        var status = root.GetProperty("status").GetString() switch
        {
            "approved" => AutoReparos.Billing.Domain.StatusPagamento.Capturado,
            "rejected" => AutoReparos.Billing.Domain.StatusPagamento.Rejeitado,
            "cancelled" => AutoReparos.Billing.Domain.StatusPagamento.Cancelado,
            "refunded" or "charged_back" => AutoReparos.Billing.Domain.StatusPagamento.Reembolsado,
            "pending" or "in_process" => AutoReparos.Billing.Domain.StatusPagamento.Pendente,
            _ => (AutoReparos.Billing.Domain.StatusPagamento?)null
        };
        return status is null ? null : (paymentId, status.Value, id);
    }, ct);

    private Task<T> RequestWithRetryAsync<T>(string alertCode, Func<HttpRequestMessage> requestFactory,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken ct) =>
        DependencyHttp.SendAsync(http, alerts, "mercado-pago", alertCode, requestFactory, read, ct, delay);
}

public static class DependencyHttp
{
    public static bool IsTransient(Exception ex) => ex is HttpRequestException { StatusCode: null } || ex is HttpRequestException requestError &&
        requestError.StatusCode is { } status && (status == System.Net.HttpStatusCode.RequestTimeout ||
        status == System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500);

    public static async Task<T> SendAsync<T>(HttpClient http, IAlertSink alerts, string dependencia, string alertCode, Func<HttpRequestMessage> requestFactory,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken ct, Func<int, TimeSpan>? delay = null)
    {
        T? result = default;
        try
        {
            await DependencyRetry.ExecuteAsync(async token =>
            {
                using var request = requestFactory(); using var response = await http.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"{dependencia} respondeu {(int)response.StatusCode}.", null, response.StatusCode);
                result = await read(response, token);
            }, token => alerts.AlertarAsync(alertCode, "Tentativas de integração esgotadas.", token), ct, delay: delay, transient: IsTransient);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsTransient(ex))
        { throw new DependenciaIndisponivelException(dependencia, ex); }
        return result!;
    }
}

public sealed class MercadoPagoSignatureValidator(IConfiguration config) : IWebhookSignatureValidator
{
    public bool IsValid(string? signature, string? requestId, string dataId)
    {
        var secret = config["MercadoPago:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(requestId)) return false;
        var fields = signature.Split(',').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim(), x => x[1].Trim());
        if (!fields.TryGetValue("ts", out var ts) || !fields.TryGetValue("v1", out var v1)) return false;
        var manifest = $"id:{dataId.ToLowerInvariant()};request-id:{requestId};ts:{ts};";
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest)));
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(v1), Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }
}

public sealed class SqsResultPublisher(IAmazonSQS sqs, IConfiguration config, ILogger<SqsResultPublisher> logger, IAlertSink alerts) : IResultadoPublisher
{
    public async Task PublicarAsync(EventoPagamento evento, CancellationToken ct)
    {
        var main = config["Sqs:QueueUrl"] ?? throw new InvalidOperationException("SQS principal não configurada.");
        var dlq = config["Sqs:DeadLetterQueueUrl"] ?? throw new InvalidOperationException("SQS DLQ não configurada.");
        var payload = JsonSerializer.Serialize(evento);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try { await sqs.SendMessageAsync(main, payload, ct); return; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 5)
                {
                    try
                    {
                        await sqs.SendMessageAsync(dlq, payload, ct);
                        await alerts.AlertarAsync("billing.sqs.exhausted", evento.PagamentoId.ToString(), ct);
                        return;
                    }
                    catch (Exception dlqError) when (dlqError is not OperationCanceledException)
                    {
                        logger.LogError(dlqError, "Falha ao encaminhar evento Billing à DLQ");
                        await alerts.AlertarAsync("billing.sqs.dlq_failed", evento.PagamentoId.ToString(), ct);
                        throw;
                    }
                }
                await Task.Delay(RetryDelayPolicy.Calculate(attempt, Random.Shared.Next(0, 100)), ct);
            }
        }
    }
}

public sealed class LoggingAlertSink(ILogger<LoggingAlertSink> logger) : IAlertSink
{
    public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { logger.LogCritical("ALERTA {Codigo}: {Detalhe}", codigo, detalhe); return Task.CompletedTask; }
}

public static class DependencyRetry
{
    public static async Task ExecuteAsync(Func<CancellationToken, Task> operation, Func<CancellationToken, Task> exhausted, CancellationToken ct,
        int maxAttempts = 5, Func<int, TimeSpan>? delay = null, Func<Exception, bool>? transient = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await operation(ct); return; }
            catch (Exception ex) when (ex is not OperationCanceledException && (transient?.Invoke(ex) ?? true))
            {
                if (attempt >= maxAttempts) { await exhausted(ct); throw; }
                var wait = delay?.Invoke(attempt) ?? RetryDelayPolicy.Calculate(attempt, Random.Shared.Next(0, 100));
                await Task.Delay(wait, ct);
            }
        }
    }
}

public static class RetryDelayPolicy
{
    public static TimeSpan Calculate(int attempt, int jitterMilliseconds)
    {
        if (attempt < 1 || jitterMilliseconds is < 0 or >= 100) throw new ArgumentOutOfRangeException();
        return TimeSpan.FromMilliseconds(Math.Min(5000, 100 * (1 << Math.Min(attempt - 1, 30)) + jitterMilliseconds));
    }
}

public sealed class OsReadinessClient(HttpClient http, IConfiguration config, IAlertSink alerts, Func<int, TimeSpan>? delay = null) : IOrderReadiness
{
    public Task<bool> ServicoConcluidoAsync(Guid ordemServicoId, CancellationToken ct)
    {
        var baseUrl = config["OsService:BaseUrl"] ?? throw new InvalidOperationException("OS Service não configurado.");
        return DependencyHttp.SendAsync(http, alerts, "os-service", "billing.os_service.readiness_failed", () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v1/ordens-servico/{ordemServicoId}");
            var serviceToken = config["OsService:BearerToken"];
            if (!string.IsNullOrWhiteSpace(serviceToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            return request;
        }, async (response, token) =>
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return string.Equals(body.RootElement.GetProperty("status").GetString(), "Concluida", StringComparison.OrdinalIgnoreCase);
        }, ct, delay);
    }
}
