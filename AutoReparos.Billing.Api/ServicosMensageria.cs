using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Billing.Application;
using AutoReparos.Billing.Infrastructure;

namespace AutoReparos.Billing.Api;

/// <summary>
/// Despachador do outbox: a cada ciclo publica as mensagens pendentes na fila de entrada da Saga. O primeiro ciclo roda ao
/// iniciar o serviço, o que retoma mensagens deixadas por um processo anterior. Falhas do SQS nunca afetam as rotas HTTP.
/// </summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IConfiguration config, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalo = TimeSpan.FromMilliseconds(config.GetValue("Outbox:IntervaloMs", 2000));
        var alertouConfiguracao = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!OutboxProcessor.Configurado(config))
                {
                    if (!alertouConfiguracao)
                    {
                        await scope.ServiceProvider.GetRequiredService<IAlertSink>().AlertarAsync("billing.outbox.nao_configurado", "Sqs:QueueUrl e Sqs:DeadLetterQueueUrl são obrigatórias para publicar mensagens da Saga.", stoppingToken);
                        alertouConfiguracao = true;
                    }
                }
                else
                {
                    alertouConfiguracao = false;
                    await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessarPendentesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogError(ex, "Falha no ciclo do despachador de outbox"); }
            try { await Task.Delay(intervalo, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>
/// Consome os comandos da Saga em <c>Sqs:ComandosQueueUrl</c> (long polling). A mensagem só é apagada depois de resolvida
/// (efeito aplicado, recusa respondida ou encaminhada à DLQ); se o processamento falhar por completo, volta à fila.
/// </summary>
public sealed class ComandosConsumer(ProcessadorComandos processador, IAmazonSQS sqs, IConfiguration config, ILogger<ComandosConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fila = config["Sqs:ComandosQueueUrl"];
        if (string.IsNullOrWhiteSpace(fila) || string.IsNullOrWhiteSpace(config["Sqs:ComandosDeadLetterQueueUrl"]))
        {
            logger.LogWarning("Sqs:ComandosQueueUrl e Sqs:ComandosDeadLetterQueueUrl não configuradas: consumo de comandos desativado.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = fila, MaxNumberOfMessages = 10, WaitTimeSeconds = 20 }, stoppingToken);
                await Task.WhenAll((resposta.Messages ?? []).Select(m => TratarAsync(fila, m, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao receber comandos da fila");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task TratarAsync(string fila, Message mensagem, CancellationToken ct)
    {
        try
        {
            await processador.ProcessarAsync(mensagem.Body, ct);
            await sqs.DeleteMessageAsync(fila, mensagem.ReceiptHandle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogError(ex, "Comando {MessageId} não resolvido; volta à fila após o visibility timeout", mensagem.MessageId); }
    }
}
