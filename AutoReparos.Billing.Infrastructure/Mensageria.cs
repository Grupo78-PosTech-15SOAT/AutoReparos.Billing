using Amazon.SQS;
using AutoReparos.Billing.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoReparos.Billing.Infrastructure;

public enum ResultadoEnvio { Enviada, EncaminhadaDlq }

/// <summary>
/// Envio ao SQS com a política padrão do Billing: 5 tentativas, backoff exponencial com jitter e, ao esgotar, DLQ e alerta.
/// Se até a DLQ falhar, alerta e propaga a exceção (o chamador mantém a mensagem pendente).
/// </summary>
public static class SqsEnvio
{
    public const int MaximoTentativas = 5;

    public static async Task<(ResultadoEnvio Resultado, int Tentativas)> EnviarAsync(IAmazonSQS sqs, IAlertSink alerts, ILogger logger,
        string fila, string dlq, string corpo, string chave, CancellationToken ct, Func<int, TimeSpan>? delay = null)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            try { await sqs.SendMessageAsync(fila, corpo, ct); return (ResultadoEnvio.Enviada, tentativa); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (tentativa < MaximoTentativas)
                {
                    await Task.Delay(delay?.Invoke(tentativa) ?? RetryDelayPolicy.Calculate(tentativa, Random.Shared.Next(0, 100)), ct);
                    continue;
                }
                try
                {
                    await sqs.SendMessageAsync(dlq, corpo, ct);
                    await alerts.AlertarAsync("billing.sqs.exhausted", chave, ct);
                    return (ResultadoEnvio.EncaminhadaDlq, tentativa);
                }
                catch (Exception dlqError) when (dlqError is not OperationCanceledException)
                {
                    logger.LogError(dlqError, "Falha ao encaminhar mensagem Billing à DLQ");
                    await alerts.AlertarAsync("billing.sqs.dlq_failed", chave, ct);
                    throw;
                }
            }
        }
    }
}

/// <summary>
/// Despacha as mensagens pendentes do outbox (<c>mensagens_saida</c>) para a fila de entrada da Saga do OS (<c>Sqs:QueueUrl</c>).
/// Pendentes são retomadas a cada ciclo, inclusive após reinício do processo. A entrega é pelo menos uma vez: o
/// <c>eventId</c> é estável e o consumidor deduplica.
/// </summary>
public sealed class OutboxProcessor(BillingDbContext db, IAmazonSQS sqs, IConfiguration config, IAlertSink alerts, ILogger<OutboxProcessor> logger, Func<int, TimeSpan>? delay = null)
{
    public const int LoteMaximo = 50;

    public static bool Configurado(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Sqs:QueueUrl"]) && !string.IsNullOrWhiteSpace(config["Sqs:DeadLetterQueueUrl"]);

    /// <summary>Devolve quantas mensagens foram resolvidas (enviadas ou encaminhadas à DLQ) neste ciclo.</summary>
    public async Task<int> ProcessarPendentesAsync(CancellationToken ct)
    {
        if (!Configurado(config)) throw new InvalidOperationException("SQS principal ou DLQ não configurada.");
        var fila = config["Sqs:QueueUrl"]!; var dlq = config["Sqs:DeadLetterQueueUrl"]!;
        var pendentes = await db.MensagensSaida.AsNoTracking().Where(m => m.EnviadoEm == null && m.EsgotadaEm == null)
            .OrderBy(m => m.CriadoEm).Take(LoteMaximo).Select(m => new { m.Id, m.Payload }).ToListAsync(ct);
        var resolvidas = 0;
        foreach (var mensagem in pendentes)
        {
            (ResultadoEnvio Resultado, int Tentativas) envio;
            try { envio = await SqsEnvio.EnviarAsync(sqs, alerts, logger, fila, dlq, mensagem.Payload, mensagem.Id.ToString(), ct, delay); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nem a DLQ aceitou (alerta já emitido): a mensagem continua pendente e volta no próximo ciclo.
                logger.LogError(ex, "Mensagem de saída {Id} continua pendente", mensagem.Id);
                break;
            }
            var agora = DateTimeOffset.UtcNow;
            var alvo = db.MensagensSaida.Where(m => m.Id == mensagem.Id);
            if (envio.Resultado == ResultadoEnvio.Enviada)
                await alvo.ExecuteUpdateAsync(s => s.SetProperty(m => m.EnviadoEm, agora).SetProperty(m => m.Tentativas, envio.Tentativas), ct);
            else
                await alvo.ExecuteUpdateAsync(s => s.SetProperty(m => m.EsgotadaEm, agora).SetProperty(m => m.Tentativas, envio.Tentativas), ct);
            resolvidas++;
        }
        return resolvidas;
    }
}

/// <summary>
/// Processa um comando recebido em <c>Sqs:ComandosQueueUrl</c> com a política padrão: cada tentativa usa um escopo novo
/// (contexto limpo), 5 tentativas com backoff e jitter e, ao esgotar, alerta e DLQ própria (<c>Sqs:ComandosDeadLetterQueueUrl</c>).
/// Mensagens sem identificadores válidos também vão à DLQ (o serviço já alertou).
/// </summary>
public sealed class ProcessadorComandos(IServiceScopeFactory scopes, IAmazonSQS sqs, IConfiguration config, ILogger<ProcessadorComandos> logger, Func<int, TimeSpan>? delay = null)
{
    /// <summary>Se retornar sem exceção a mensagem foi resolvida e pode ser apagada da fila; se lançar, deve voltar à fila.</summary>
    public async Task<DesfechoComando> ProcessarAsync(string corpo, CancellationToken ct)
    {
        var dlq = config["Sqs:ComandosDeadLetterQueueUrl"] ?? throw new InvalidOperationException("DLQ de comandos não configurada.");
        var desfecho = DesfechoComando.DeadLetter;
        try
        {
            await DependencyRetry.ExecuteAsync(async token =>
            {
                await using var scope = scopes.CreateAsyncScope();
                desfecho = await scope.ServiceProvider.GetRequiredService<CancelamentoOrcamentoService>().ProcessarAsync(corpo, token);
            }, async token =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAlertSink>().AlertarAsync("billing.comando.esgotado", "Tentativas de processar o comando de cancelamento esgotadas; encaminhado à DLQ.", token);
            }, ct, delay: delay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Comando de cancelamento esgotou as tentativas");
            desfecho = DesfechoComando.DeadLetter;
        }
        if (desfecho != DesfechoComando.DeadLetter) return desfecho;
        try { await sqs.SendMessageAsync(dlq, corpo, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao encaminhar comando à DLQ");
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAlertSink>().AlertarAsync("billing.comando.dlq_failed", "Falha ao encaminhar comando à DLQ de comandos.", ct);
            throw;
        }
        return desfecho;
    }
}
