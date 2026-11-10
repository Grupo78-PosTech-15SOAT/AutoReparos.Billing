using AutoReparos.Billing.Domain;

namespace AutoReparos.Billing.Application;

public sealed record CriarOrcamento(Guid OrdemServicoId, Guid ClienteId, decimal Valor);
public sealed record AbrirCheckout(string Url, string Referencia);
// Contrato pagamento.resultado.v1 (envelope completo); a construção fica em MensagensSaga.ResultadoPagamento.
public sealed record EventoPagamento(Guid EventId, string Tipo, Guid OrdemServicoId, DateTime OcorreuEm, Guid PagamentoId, string Status, string Referencia);
public sealed record SolicitacaoReembolso(decimal Valor, string Referencia);

public sealed class ConcorrenciaException(string mensagem, Exception? inner = null) : Exception(mensagem, inner);
public sealed class PublicacaoResultadoException(string mensagem, Exception? inner = null) : InvalidOperationException(mensagem, inner);
public sealed class RespostaProvedorInvalidaException(string provedor, string detalhe, Exception? inner = null) : Exception($"Resposta inválida de {provedor}: {detalhe}", inner)
{
    public string Provedor { get; } = provedor;
}
public sealed class DependenciaIndisponivelException(string dependencia, Exception? inner = null) : Exception($"Dependência indisponível: {dependencia}.", inner)
{
    public string Dependencia { get; } = dependencia;
}

public interface IBillingStore
{
    Task<Orcamento?> ObterOrcamentoAsync(Guid id, CancellationToken ct);
    Task<Pagamento?> ObterPagamentoAsync(Guid id, CancellationToken ct);
    Task<Orcamento?> ObterPorOrdemAsync(Guid ordemId, CancellationToken ct);
    Task<Orcamento?> ObterPorPagamentoAsync(Guid pagamentoId, CancellationToken ct);
    Task AdicionarOrcamentoAsync(Orcamento orcamento, CancellationToken ct);
    /// <summary>Registra uma mensagem de outbox na mesma unidade de trabalho do próximo <see cref="SalvarAsync"/>.</summary>
    void AdicionarMensagemSaida(MensagemSaida mensagem);
    Task SalvarAsync(CancellationToken ct);
}
public interface ICheckoutProvider { Task<AbrirCheckout> CriarCheckoutAsync(Guid pagamentoId, decimal valor, string idempotencyKey, CancellationToken ct); Task<string> ReembolsarAsync(string referencia, decimal valor, string idempotencyKey, CancellationToken ct); Task<(Guid PagamentoId, StatusPagamento Status, string Referencia)?> ConsultarPagamentoAsync(string id, CancellationToken ct); }
public interface IResultadoPublisher { Task PublicarAsync(EventoPagamento evento, CancellationToken ct); }
public interface IWebhookSignatureValidator { bool IsValid(string? signature, string? requestId, string dataId); }
public interface IAlertSink { Task AlertarAsync(string codigo, string detalhe, CancellationToken ct); }
public interface IOrderReadiness { Task<bool> ServicoConcluidoAsync(Guid ordemServicoId, CancellationToken ct); }

public sealed class BillingService(IBillingStore store, ICheckoutProvider provider, IResultadoPublisher publisher, IAlertSink alerts, IOrderReadiness readiness)
{
    public async Task<Orcamento> CriarOrcamentoAsync(CriarOrcamento request, CancellationToken ct)
    {
        var existente = await store.ObterPorOrdemAsync(request.OrdemServicoId, ct);
        if (existente is not null) return existente;
        var orcamento = new Orcamento(request.OrdemServicoId, request.ClienteId, request.Valor, DateTimeOffset.UtcNow);
        await store.AdicionarOrcamentoAsync(orcamento, ct); await store.SalvarAsync(ct); return orcamento;
    }
    public async Task<Orcamento> AprovarOrcamentoAsync(Guid id, Guid clienteId, CancellationToken ct)
    {
        var orcamento = await store.ObterOrcamentoAsync(id, ct) ?? throw new KeyNotFoundException();
        if (orcamento.ClienteId != clienteId) throw new UnauthorizedAccessException();
        orcamento.Aprovar();
        // Outbox: o evento da Saga é gravado na mesma transação da aprovação; o despachador publica depois, sem afetar o HTTP.
        store.AdicionarMensagemSaida(MensagensSaga.OrcamentoAprovado(orcamento, DateTime.UtcNow));
        await store.SalvarAsync(ct); return orcamento;
    }
    public async Task<Pagamento> AbrirCheckoutAsync(Guid pagamentoId, Guid? clienteId, CancellationToken ct)
    {
        var pagamento = await store.ObterPagamentoAsync(pagamentoId, ct) ?? throw new KeyNotFoundException();
        var orcamento = await store.ObterPorPagamentoAsync(pagamentoId, ct) ?? throw new KeyNotFoundException();
        if (clienteId is not null && orcamento.ClienteId != clienteId) throw new UnauthorizedAccessException();
        if (pagamento.Status == StatusPagamento.Pendente) return pagamento;
        if (pagamento.Status == StatusPagamento.Capturado) throw new InvalidOperationException("Pagamento já capturado.");
        if (orcamento.Status != StatusOrcamento.Aprovado || !await readiness.ServicoConcluidoAsync(orcamento.OrdemServicoId, ct))
            throw new InvalidOperationException("Cobrança exige orçamento aprovado e serviço concluído.");
        var checkout = await provider.CriarCheckoutAsync(pagamento.Id, pagamento.Valor, pagamento.ChaveIdempotenciaCheckout, ct);
        pagamento.IniciarCheckout(checkout.Url, checkout.Referencia); await store.SalvarAsync(ct); return pagamento;
    }
    public async Task AplicarWebhookAsync(Guid pagamentoId, StatusPagamento status, string referencia, CancellationToken ct)
    {
        var pagamento = await store.ObterPagamentoAsync(pagamentoId, ct) ?? throw new KeyNotFoundException();
        if (pagamento.Status == status && pagamento.ReferenciaProvedor == referencia && pagamento.ResultadoPublicado) return;
        var ordemServicoId = (await store.ObterPorPagamentoAsync(pagamentoId, ct) ?? throw new KeyNotFoundException()).OrdemServicoId;
        if (pagamento.Status != status || pagamento.ReferenciaProvedor != referencia)
        {
            if (!pagamento.AplicarEstado(status, referencia)) return;
            await store.SalvarAsync(ct);
        }
        try { await publisher.PublicarAsync(MensagensSaga.ResultadoPagamento(ordemServicoId, pagamento.Id, status, referencia, DateTime.UtcNow), ct); pagamento.MarcarResultadoPublicado(); await store.SalvarAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await alerts.AlertarAsync("billing.payment_sync_failed", $"{pagamento.Id}: {ex.Message}", ct);
            throw new PublicacaoResultadoException($"Falha ao publicar o resultado do pagamento {pagamento.Id}.", ex);
        }
    }
    public async Task<Reembolso> ReembolsarAsync(Guid pagamentoId, decimal valor, string aprovador, string referencia, CancellationToken ct)
    {
        var pagamento = await store.ObterPagamentoAsync(pagamentoId, ct) ?? throw new KeyNotFoundException();
        var providerReference = pagamento.ReferenciaProvedor ?? throw new InvalidOperationException("Referência de provedor ausente.");
        var existente = pagamento.ObterReembolso(referencia);
        Reembolso reembolso;
        if (existente is null)
        {
            // Passo 1: reserva Pendente, ja conta no limite cumulativo e nao muda o status do pagamento.
            reembolso = pagamento.Reembolsar(valor, aprovador, referencia, DateTimeOffset.UtcNow);
            await store.SalvarAsync(ct);
        }
        else if (existente.Estado == EstadoReembolso.Pendente && existente.Valor == valor)
            reembolso = existente; // retomada: mesma referencia, mesma chave de idempotencia
        else throw new InvalidOperationException("Reembolso inválido.");
        var key = "refund-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{pagamentoId:N}:{referencia}")));
        try { await provider.ReembolsarAsync(providerReference, valor, key, ct); }
        catch (Exception ex) when (!FalhaDefinitiva(ex))
        {
            // Desfecho incerto: o provedor pode ter processado. Mantem a reserva Pendente e propaga.
            await alerts.AlertarAsync("billing.refund_outcome_unknown", $"{pagamentoId}/{referencia}: {ex.GetType().Name}", CancellationToken.None);
            throw;
        }
        catch
        {
            pagamento.DesfazerReembolso(reembolso);
            try { await store.SalvarAsync(CancellationToken.None); }
            catch (Exception ex) { await alerts.AlertarAsync("billing.refund_compensation_failed", $"{pagamentoId}: {ex.Message}", CancellationToken.None); }
            throw;
        }
        pagamento.ConfirmarReembolso(reembolso);
        await store.SalvarAsync(CancellationToken.None);
        return reembolso;
    }
    private static bool FalhaDefinitiva(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500 &&
        status is not (System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests);
}
