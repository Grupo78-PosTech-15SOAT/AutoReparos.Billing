using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoReparos.Billing.Domain;

namespace AutoReparos.Billing.Application;

/// <summary>Valores de <c>tipo</c> do contrato de mensagens da Saga (v1).</summary>
public static class TiposMensagem
{
    public const string OrcamentoAprovado = "orcamento.aprovado.v1";
    public const string PagamentoResultado = "pagamento.resultado.v1";
    public const string OrcamentoCancelado = "orcamento.cancelado.v1";
    public const string OrcamentoCancelamentoRecusado = "orcamento.cancelamento-recusado.v1";
    public const string CancelarOrcamento = "cobranca.cancelar-orcamento.v1";
    public const string CancelarOrcamentoLegado = "billing.orcamento.cancelar.v1";

    public static bool EhCancelamentoOrcamento(string? tipo) =>
        tipo is CancelarOrcamento or CancelarOrcamentoLegado;
}

// Envelopes de saída. A ordem das propriedades é a ordem do JSON; correlacaoId é omitido quando ausente.
public sealed record EnvelopeOrcamentoAprovado(Guid EventId, string Tipo, Guid OrdemServicoId, DateTime OcorreuEm, Guid OrcamentoId, Guid ClienteId);
public sealed record EnvelopeOrcamentoCancelado(Guid EventId, string Tipo, Guid? CorrelacaoId, Guid OrdemServicoId, DateTime OcorreuEm, Guid OrcamentoId);
public sealed record EnvelopeCancelamentoRecusado(Guid EventId, string Tipo, Guid? CorrelacaoId, Guid OrdemServicoId, DateTime OcorreuEm, Guid OrcamentoId, string Motivo);

public static class MensagensSaga
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Id determinístico (a partir de uma semente), para que reenvios e reprocessamentos mantenham o mesmo <c>eventId</c>.</summary>
    public static Guid IdDeterministico(string semente) => new(SHA256.HashData(Encoding.UTF8.GetBytes(semente))[..16]);

    public static MensagemSaida OrcamentoAprovado(Orcamento orcamento, DateTime ocorreuEmUtc)
    {
        var id = IdDeterministico($"{TiposMensagem.OrcamentoAprovado}:{orcamento.Id}");
        var envelope = new EnvelopeOrcamentoAprovado(id, TiposMensagem.OrcamentoAprovado, orcamento.OrdemServicoId, ocorreuEmUtc, orcamento.Id, orcamento.ClienteId);
        return new MensagemSaida(id, envelope.Tipo, JsonSerializer.Serialize(envelope, Json), ocorreuEmUtc);
    }

    public static MensagemSaida OrcamentoCancelado(Guid eventId, Guid? correlacaoId, Guid ordemServicoId, Guid orcamentoId, DateTime ocorreuEmUtc)
    {
        var envelope = new EnvelopeOrcamentoCancelado(eventId, TiposMensagem.OrcamentoCancelado, correlacaoId, ordemServicoId, ocorreuEmUtc, orcamentoId);
        return new MensagemSaida(eventId, envelope.Tipo, JsonSerializer.Serialize(envelope, Json), ocorreuEmUtc);
    }

    public static MensagemSaida CancelamentoRecusado(Guid eventId, Guid? correlacaoId, Guid ordemServicoId, Guid orcamentoId, string motivo, DateTime ocorreuEmUtc)
    {
        var envelope = new EnvelopeCancelamentoRecusado(eventId, TiposMensagem.OrcamentoCancelamentoRecusado, correlacaoId, ordemServicoId, ocorreuEmUtc, orcamentoId, motivo);
        return new MensagemSaida(eventId, envelope.Tipo, JsonSerializer.Serialize(envelope, Json), ocorreuEmUtc);
    }

    /// <summary>eventId estável por pagamento + status + referência: a republicação após falha mantém o mesmo identificador.</summary>
    public static EventoPagamento ResultadoPagamento(Guid ordemServicoId, Guid pagamentoId, StatusPagamento status, string referencia, DateTime ocorreuEmUtc) =>
        new(IdDeterministico($"{TiposMensagem.PagamentoResultado}:{pagamentoId}:{status}:{referencia}"), TiposMensagem.PagamentoResultado,
            ordemServicoId, ocorreuEmUtc, pagamentoId, status.ToString(), referencia);
}

public interface IEventosProcessadosStore
{
    Task<bool> ExisteAsync(Guid eventId, CancellationToken ct);
    void Registrar(EventoProcessado evento);
}

public enum DesfechoComando { Cancelado, Recusado, Duplicado, DeadLetter }

/// <summary>Campos lidos de um comando de cancelamento; o que não for válido fica nulo.</summary>
public sealed record ComandoCancelamento(string? Tipo, Guid? EventId, Guid? CorrelacaoId, Guid? OrdemServicoId, Guid? OrcamentoId)
{
    public static ComandoCancelamento Ler(string corpo)
    {
        JsonDocument documento;
        try { documento = JsonDocument.Parse(corpo); }
        catch (JsonException) { return new(null, null, null, null, null); }
        using (documento)
        {
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return new(null, null, null, null, null);
            return new(Texto(raiz, "tipo"), Identificador(raiz, "eventId"), Identificador(raiz, "correlacaoId"), Identificador(raiz, "ordemServicoId"), Identificador(raiz, "orcamentoId"));
        }
    }

    private static string? Texto(JsonElement raiz, string nome) =>
        raiz.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static Guid? Identificador(JsonElement raiz, string nome) =>
        Guid.TryParse(Texto(raiz, nome), out var id) && id != Guid.Empty ? id : null;
}

/// <summary>
/// Trata o comando de cancelamento em uma única transação: registro do eventId, efeito no orçamento e mensagem de
/// resposta (outbox). Nunca chama o provedor de pagamento: não há estorno por nenhum caminho.
/// </summary>
public sealed class CancelamentoOrcamentoService(IBillingStore store, IEventosProcessadosStore processados, IAlertSink alerts)
{
    public async Task<DesfechoComando> ProcessarAsync(string corpo, CancellationToken ct)
    {
        var comando = ComandoCancelamento.Ler(corpo);
        if (comando.OrcamentoId is not { } orcamentoId || comando.OrdemServicoId is not { } ordemServicoId)
        {
            await alerts.AlertarAsync("billing.comando.malformado", $"Comando sem orcamentoId e ordemServicoId válidos (eventId {comando.EventId?.ToString() ?? "ausente"}); encaminhado à DLQ.", ct);
            return DesfechoComando.DeadLetter;
        }
        // Sem eventId válido não há deduplicação pelo contrato: usa-se um identificador derivado do corpo, estável entre entregas.
        var chave = comando.EventId ?? MensagensSaga.IdDeterministico("comando-sem-evento:" + corpo);
        if (await processados.ExisteAsync(chave, ct)) return DesfechoComando.Duplicado;

        string? motivoRecusa = null, alerta = null; Orcamento? orcamento = null;
        var tipoCancelamento = TiposMensagem.EhCancelamentoOrcamento(comando.Tipo);
        if (!tipoCancelamento) { motivoRecusa = "tipo_desconhecido"; alerta = "billing.comando.tipo_desconhecido"; }
        else if (comando.EventId is null) { motivoRecusa = "mensagem_malformada"; alerta = "billing.comando.malformado"; }
        else
        {
            orcamento = await store.ObterOrcamentoAsync(orcamentoId, ct);
            if (orcamento is null) { motivoRecusa = "orcamento_inexistente"; alerta = "billing.comando.orcamento_inexistente"; }
            else if (orcamento.OrdemServicoId != ordemServicoId) { motivoRecusa = "ordem_divergente"; alerta = "billing.comando.ordem_divergente"; }
            else orcamento.PodeCancelar(out motivoRecusa); // recusa de negócio: sem alerta técnico; a Saga alerta ao recebê-la
        }

        var agora = DateTime.UtcNow;
        if (motivoRecusa is null)
        {
            orcamento!.Cancelar();
            store.AdicionarMensagemSaida(MensagensSaga.OrcamentoCancelado(MensagensSaga.IdDeterministico($"{chave}:{TiposMensagem.OrcamentoCancelado}"), comando.CorrelacaoId, ordemServicoId, orcamentoId, agora));
        }
        else
            store.AdicionarMensagemSaida(MensagensSaga.CancelamentoRecusado(MensagensSaga.IdDeterministico($"{chave}:{TiposMensagem.OrcamentoCancelamentoRecusado}"), comando.CorrelacaoId, ordemServicoId, orcamentoId, motivoRecusa, agora));
        processados.Registrar(new EventoProcessado(chave, tipoCancelamento ? comando.Tipo! : "desconhecido", agora));
        // Duplicata concorrente: a violação de chave única vira ConcorrenciaException e o reprocessamento encontra o eventId registrado.
        await store.SalvarAsync(ct);
        if (alerta is not null) await alerts.AlertarAsync(alerta, $"Comando {chave} recusado: {motivoRecusa} (orçamento {orcamentoId}).", ct);
        return motivoRecusa is null ? DesfechoComando.Cancelado : DesfechoComando.Recusado;
    }
}
