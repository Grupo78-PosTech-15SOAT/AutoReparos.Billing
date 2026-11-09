using AutoReparos.Billing.Domain;

namespace AutoReparos.Billing.Application;

public sealed record CriarOrcamento(Guid OrdemServicoId, Guid ClienteId, decimal Valor);
public sealed record AbrirCheckout(string Url, string Referencia);
public sealed record EventoPagamento(Guid PagamentoId, string Status, string Referencia);
public sealed record SolicitacaoReembolso(decimal Valor, string Referencia);

public sealed class ConcorrenciaException(string mensagem, Exception? inner = null) : Exception(mensagem, inner);
public sealed class PublicacaoResultadoException(string mensagem, Exception? inner = null) : InvalidOperationException(mensagem, inner);
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
    Task SalvarAsync(CancellationToken ct);
}
public interface ICheckoutProvider { Task<AbrirCheckout> CriarCheckoutAsync(Guid pagamentoId, decimal valor, CancellationToken ct); Task<string> ReembolsarAsync(string referencia, decimal valor, string idempotencyKey, CancellationToken ct); Task<(Guid PagamentoId, StatusPagamento Status, string Referencia)?> ConsultarPagamentoAsync(string id, CancellationToken ct); }
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
        orcamento.Aprovar(); await store.SalvarAsync(ct); return orcamento;
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
        var checkout = await provider.CriarCheckoutAsync(pagamento.Id, pagamento.Valor, ct);
        pagamento.IniciarCheckout(checkout.Url, checkout.Referencia); await store.SalvarAsync(ct); return pagamento;
    }
    public async Task AplicarWebhookAsync(Guid pagamentoId, StatusPagamento status, string referencia, CancellationToken ct)
    {
        var pagamento = await store.ObterPagamentoAsync(pagamentoId, ct) ?? throw new KeyNotFoundException();
        if (pagamento.Status == status && pagamento.ReferenciaProvedor == referencia && pagamento.ResultadoPublicado) return;
        if (pagamento.Status != status || pagamento.ReferenciaProvedor != referencia)
        {
            if (!pagamento.AplicarEstado(status, referencia)) return;
            await store.SalvarAsync(ct);
        }
        try { await publisher.PublicarAsync(new(pagamento.Id, status.ToString(), referencia), ct); pagamento.MarcarResultadoPublicado(); await store.SalvarAsync(ct); }
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
        var reembolso = pagamento.Reembolsar(valor, aprovador, referencia, DateTimeOffset.UtcNow);
        var key = "refund-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{pagamentoId:N}:{referencia}")));
        await store.SalvarAsync(ct);
        try { await provider.ReembolsarAsync(providerReference, valor, key, ct); }
        catch
        {
            pagamento.DesfazerReembolso(reembolso);
            try { await store.SalvarAsync(CancellationToken.None); }
            catch (Exception ex) { await alerts.AlertarAsync("billing.refund_compensation_failed", $"{pagamentoId}: {ex.Message}", CancellationToken.None); }
            throw;
        }
        return reembolso;
    }
}
