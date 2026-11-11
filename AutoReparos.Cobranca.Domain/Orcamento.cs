namespace AutoReparos.Cobranca.Domain;

public sealed class Orcamento
{
    private Orcamento() { }
    public Orcamento(Guid ordemServicoId, Guid clienteId, decimal valor, DateTimeOffset criadoEm)
    {
        if (ordemServicoId == Guid.Empty || clienteId == Guid.Empty || valor <= 0 || valor > LimitesBilling.ValorMaximo || decimal.Round(valor, 2) != valor)
            throw new ArgumentException("Orçamento inválido.");
        Id = Guid.NewGuid(); OrdemServicoId = ordemServicoId; ClienteId = clienteId; Valor = valor; CriadoEm = criadoEm;
        Status = StatusOrcamento.Pendente;
    }
    public Guid Id { get; private set; }
    public Guid OrdemServicoId { get; private set; }
    public Guid ClienteId { get; private set; }
    public decimal Valor { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public StatusOrcamento Status { get; private set; }
    public Pagamento? Pagamento { get; private set; }
    public void Aprovar() { if (Status != StatusOrcamento.Pendente) throw new InvalidOperationException("Orçamento não está pendente."); Status = StatusOrcamento.Aprovado; Pagamento ??= new Pagamento(Id, Valor); }
    /// <summary>
    /// O cancelamento só é possível enquanto não houve cobrança: orçamento Pendente, ou Aprovado cujo pagamento ainda está
    /// SemTentativa (sem checkout iniciado e sem reembolso). Cancelar um orçamento já Cancelado é idempotente.
    /// </summary>
    public bool PodeCancelar(out string? motivoRecusa)
    {
        motivoRecusa = null;
        if (Status == StatusOrcamento.Cancelado) return true;
        if (Status == StatusOrcamento.Recusado) { motivoRecusa = "orcamento_recusado"; return false; }
        if (Pagamento is { } p && (p.Status != StatusPagamento.SemTentativa || p.Tentativas > 0 || p.Reembolsos.Count > 0))
        { motivoRecusa = "pagamento_existente"; return false; }
        return true;
    }
    public void Cancelar()
    {
        if (!PodeCancelar(out var motivo)) throw new InvalidOperationException($"Cancelamento recusado: {motivo}.");
        if (Status == StatusOrcamento.Cancelado) return;
        Status = StatusOrcamento.Cancelado;
        // Toca o pagamento para que um checkout concorrente (que altera a mesma linha) seja detectado pelo token xmin.
        Pagamento?.RegistrarCancelamentoDoOrcamento();
    }
    public Pagamento CriarPagamento(bool servicoConcluido)
    {
        if (Status != StatusOrcamento.Aprovado || !servicoConcluido) throw new InvalidOperationException("Cobrança exige orçamento aprovado e serviço concluído.");
        return Pagamento ??= new Pagamento(Id, Valor);
    }
    public void DefinirPagamento(Pagamento pagamento) => Pagamento = pagamento;
}

public enum StatusOrcamento { Pendente, Aprovado, Recusado, Cancelado }
