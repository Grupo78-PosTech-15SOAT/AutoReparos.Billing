using AutoReparos.Cobranca.Domain;

namespace AutoReparos.Cobranca.Application;

// Com `Itens` o valor é calculado pelos preços do catálogo da Execução (snapshot) e `Valor` deve ficar em 0; sem `Itens` vale o `Valor` informado.
public sealed record CriarOrcamento(Guid OrdemServicoId, Guid ClienteId, decimal Valor = 0, IReadOnlyList<ItemOrcamentoSolicitado>? Itens = null);
public sealed record ItemOrcamentoSolicitado(string Tipo, Guid ReferenciaId, int Quantidade)
{
    public bool TentarTipo(out TipoItemOrcamento tipo)
    {
        tipo = default;
        if (string.Equals(Tipo, "servico", StringComparison.OrdinalIgnoreCase)) tipo = TipoItemOrcamento.Servico;
        else if (string.Equals(Tipo, "insumo", StringComparison.OrdinalIgnoreCase)) tipo = TipoItemOrcamento.Insumo;
        else return false;
        return true;
    }

    // Itens repetidos (mesmo tipo e referência), quantidade fora da faixa e lista acima do limite são inválidos.
    public static bool ListaValida(IReadOnlyList<ItemOrcamentoSolicitado> itens) =>
        itens.Count <= LimitesItensOrcamento.ItensMaximos
        && itens.All(i => i is not null && i.TentarTipo(out _) && i.ReferenciaId != Guid.Empty && i.Quantidade is >= 1 and <= LimitesItensOrcamento.QuantidadeMaxima)
        && itens.Select(i => (i.Tipo.ToLowerInvariant(), i.ReferenciaId)).Distinct().Count() == itens.Count;
}
public sealed class ItemCatalogoInexistenteException(string tipo, Guid referenciaId) : Exception($"Item de catálogo inexistente: {tipo} {referenciaId}.")
{
    public string Tipo { get; } = tipo;
    public Guid ReferenciaId { get; } = referenciaId;
}
// Preços vigentes do catálogo da Execução; devolve um item por solicitação, na mesma ordem, ou lança ItemCatalogoInexistenteException.
public interface ICatalogoPrecos { Task<IReadOnlyList<ItemOrcamentoPreco>> ConsultarAsync(IReadOnlyList<ItemOrcamentoSolicitado> itens, CancellationToken ct); }
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

public sealed record LinkAprovacao(string Token, DateTimeOffset ExpiraEm);
/// <summary>Token de aprovação desconhecido, malformado, expirado (ou já usado, no resumo); as causas são indistinguíveis de propósito.</summary>
public sealed class TokenInvalidoException : Exception;

public interface IBillingStore
{
    Task<Orcamento?> ObterOrcamentoAsync(Guid id, CancellationToken ct);
    Task<Pagamento?> ObterPagamentoAsync(Guid id, CancellationToken ct);
    Task<Orcamento?> ObterPorOrdemAsync(Guid ordemId, CancellationToken ct);
    Task<Orcamento?> ObterPorPagamentoAsync(Guid pagamentoId, CancellationToken ct);
    Task<TokenAprovacao?> ObterTokenPorHashAsync(string hash, CancellationToken ct);
    Task<TokenAprovacao?> ObterTokenDoOrcamentoAsync(Guid orcamentoId, CancellationToken ct);
    void AdicionarToken(TokenAprovacao token);
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

public sealed class BillingService(IBillingStore store, ICheckoutProvider provider, IResultadoPublisher publisher, IAlertSink alerts, IOrderReadiness readiness, ICatalogoPrecos? catalogo = null)
{
    public async Task<Orcamento> CriarOrcamentoAsync(CriarOrcamento request, CancellationToken ct)
    {
        var existente = await store.ObterPorOrdemAsync(request.OrdemServicoId, ct);
        if (existente is not null) return existente;
        Orcamento orcamento;
        if (request.Itens is { Count: > 0 } itens)
        {
            // Snapshot: os preços são lidos agora e gravados no orçamento; reajustes posteriores não o alteram.
            var precos = await (catalogo ?? throw new InvalidOperationException("Catálogo de preços não configurado.")).ConsultarAsync(itens, ct);
            orcamento = new Orcamento(request.OrdemServicoId, request.ClienteId, precos, DateTimeOffset.UtcNow);
        }
        else orcamento = new Orcamento(request.OrdemServicoId, request.ClienteId, request.Valor, DateTimeOffset.UtcNow);
        await store.AdicionarOrcamentoAsync(orcamento, ct); await store.SalvarAsync(ct); return orcamento;
    }
    public async Task<Orcamento> AprovarOrcamentoAsync(Guid id, Guid clienteId, CancellationToken ct)
    {
        var orcamento = await store.ObterOrcamentoAsync(id, ct) ?? throw new KeyNotFoundException();
        if (orcamento.ClienteId != clienteId) throw new UnauthorizedAccessException();
        return await AprovarEGravarAsync(orcamento, null, ct);
    }
    private async Task<Orcamento> AprovarEGravarAsync(Orcamento orcamento, TokenAprovacao? token, CancellationToken ct)
    {
        orcamento.Aprovar();
        token?.MarcarUsado(DateTimeOffset.UtcNow);
        // Outbox: o evento da Saga é gravado na mesma transação da aprovação; o despachador publica depois, sem afetar o HTTP.
        store.AdicionarMensagemSaida(MensagensSaga.OrcamentoAprovado(orcamento, DateTime.UtcNow));
        await store.SalvarAsync(ct); return orcamento;
    }
    /// <summary>Gera o link de aprovação de um orçamento Pendente; o token anterior deixa de valer e só o hash fica guardado.</summary>
    public async Task<LinkAprovacao> GerarLinkAprovacaoAsync(Guid orcamentoId, CancellationToken ct)
    {
        var orcamento = await store.ObterOrcamentoAsync(orcamentoId, ct) ?? throw new KeyNotFoundException();
        if (orcamento.Status != StatusOrcamento.Pendente) throw new InvalidOperationException("Orçamento não está pendente.");
        var token = TokenAprovacao.GerarToken(); var hash = TokenAprovacao.CalcularHash(token); var agora = DateTimeOffset.UtcNow;
        var registro = await store.ObterTokenDoOrcamentoAsync(orcamentoId, ct);
        if (registro is null) { registro = new TokenAprovacao(orcamentoId, hash, agora); store.AdicionarToken(registro); }
        else registro.Renovar(hash, agora);
        await store.SalvarAsync(ct);
        return new LinkAprovacao(token, registro.ExpiraEm);
    }
    /// <summary>Orçamento do link, para o resumo público; token malformado, desconhecido, expirado ou usado lança <see cref="TokenInvalidoException"/>.</summary>
    public async Task<Orcamento> ObterOrcamentoPorTokenAsync(string? token, CancellationToken ct)
    {
        var (registro, orcamento) = await ResolverTokenAsync(token, ct);
        if (registro.UsadoEm is not null) throw new TokenInvalidoException();
        return orcamento;
    }
    /// <summary>Aprova pelo link: o token prova a posse, então não há checagem de dono; token já usado ou orçamento fora de Pendente lança <see cref="InvalidOperationException"/>.</summary>
    public async Task<Orcamento> AprovarPorTokenAsync(string? token, CancellationToken ct)
    {
        var (registro, orcamento) = await ResolverTokenAsync(token, ct);
        if (registro.UsadoEm is not null) throw new InvalidOperationException("Token de aprovação já utilizado.");
        return await AprovarEGravarAsync(orcamento, registro, ct);
    }
    private async Task<(TokenAprovacao Token, Orcamento Orcamento)> ResolverTokenAsync(string? token, CancellationToken ct)
    {
        if (!TokenAprovacao.FormatoValido(token)) throw new TokenInvalidoException();
        var registro = await store.ObterTokenPorHashAsync(TokenAprovacao.CalcularHash(token!), ct);
        if (registro is null || registro.ExpiraEm <= DateTimeOffset.UtcNow) throw new TokenInvalidoException();
        var orcamento = await store.ObterOrcamentoAsync(registro.OrcamentoId, ct) ?? throw new TokenInvalidoException();
        return (registro, orcamento);
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
