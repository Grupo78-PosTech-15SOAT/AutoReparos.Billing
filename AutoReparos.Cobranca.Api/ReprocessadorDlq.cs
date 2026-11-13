using Amazon.SQS;
using Amazon.SQS.Model;
using AutoReparos.Cobranca.Application;

namespace AutoReparos.Cobranca.Api;

public sealed record ResultadoReprocessamentoDlq(int Solicitadas, int Reprocessadas);

/// <summary>
/// Reprocessa as DLQs da Cobrança: a de comandos (<c>Sqs:ComandosDeadLetterQueueUrl</c>) volta à fila de comandos e a de eventos
/// publicados (<c>Sqs:DeadLetterQueueUrl</c>) volta à fila da Saga (<c>Sqs:QueueUrl</c>). A mensagem só sai da DLQ depois de reenviada;
/// a ação é registrada com o autor (alerta <c>billing.dlq-reprocessada</c>).
/// </summary>
public sealed class ReprocessadorDlq(IAmazonSQS sqs, IConfiguration configuration, IAlertSink alerts)
{
    public const int QuantidadeMaxima = 100;

    private static readonly (string Dlq, string Origem)[] Filas =
    [
        ("Sqs:ComandosDeadLetterQueueUrl", "Sqs:ComandosQueueUrl"),
        ("Sqs:DeadLetterQueueUrl", "Sqs:QueueUrl")
    ];

    public async Task<ResultadoReprocessamentoDlq> ReprocessarAsync(int quantidade, string autor, CancellationToken ct)
    {
        var reprocessadas = 0;
        foreach (var (chaveDlq, chaveOrigem) in Filas)
        {
            var dlq = configuration[chaveDlq];
            var origem = configuration[chaveOrigem];
            if (string.IsNullOrWhiteSpace(dlq) || string.IsNullOrWhiteSpace(origem)) continue;
            reprocessadas += await DevolverAsync(dlq, origem, quantidade - reprocessadas, ct);
            if (reprocessadas >= quantidade) break;
        }
        await alerts.AlertarAsync("billing.dlq-reprocessada", $"DLQ da Cobrança reprocessada por {autor}: {reprocessadas} de {quantidade} solicitadas devolvidas à fila de origem.", ct);
        return new ResultadoReprocessamentoDlq(quantidade, reprocessadas);
    }

    private async Task<int> DevolverAsync(string dlq, string origem, int limite, CancellationToken ct)
    {
        var devolvidas = 0;
        while (devolvidas < limite)
        {
            var resposta = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlq, MaxNumberOfMessages = Math.Min(10, limite - devolvidas), WaitTimeSeconds = 0 }, ct);
            if (resposta.Messages is not { Count: > 0 } mensagens) break;
            foreach (var mensagem in mensagens)
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = origem, MessageBody = mensagem.Body }, ct);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlq, ReceiptHandle = mensagem.ReceiptHandle }, ct);
                devolvidas++;
            }
        }
        return devolvidas;
    }
}
