namespace AutoReparos.Billing.Domain;

public sealed class Pagamento
{
    private readonly List<Reembolso> _reembolsos = [];
    private Pagamento() { }
    public Pagamento(Guid orcamentoId, decimal valor) { Id = Guid.NewGuid(); OrcamentoId = orcamentoId; Valor = valor; Status = StatusPagamento.SemTentativa; }
    public Guid Id { get; private set; }
    public Guid OrcamentoId { get; private set; }
    public decimal Valor { get; private set; }
    public StatusPagamento Status { get; private set; }
    public string? UrlCheckout { get; private set; }
    public string? ReferenciaProvedor { get; private set; }
    public bool ResultadoPublicado { get; private set; }
    public int Revisao { get; private set; }
    public int Tentativas { get; private set; }
    public string ChaveIdempotenciaCheckout => $"checkout-{Id:N}-{Tentativas + 1}";
    public ICollection<Reembolso> Reembolsos => _reembolsos;
    public void IniciarCheckout(string url, string referencia)
    {
        if (Status is StatusPagamento.Pendente or StatusPagamento.Capturado or StatusPagamento.Reembolsado) throw new InvalidOperationException("Já existe tentativa ativa, pagamento capturado ou reembolsado.");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(referencia)) throw new ArgumentException("Checkout e referência do provedor são obrigatórios.");
        UrlCheckout = url; ReferenciaProvedor = referencia; Status = StatusPagamento.Pendente; Tentativas++; Revisao++;
    }
    public bool AplicarEstado(StatusPagamento status, string referencia)
    {
        if (status == StatusPagamento.Pendente && Status is StatusPagamento.Rejeitado or StatusPagamento.Cancelado or StatusPagamento.Expirado) return false;
        if (Status == StatusPagamento.Capturado && status != StatusPagamento.Capturado) throw new InvalidOperationException("Pagamento capturado é terminal.");
        if (Status == StatusPagamento.Reembolsado && status != StatusPagamento.Reembolsado) throw new InvalidOperationException("Pagamento reembolsado é terminal.");
        if (status is StatusPagamento.Rejeitado or StatusPagamento.Cancelado or StatusPagamento.Expirado && Status != StatusPagamento.Pendente && status != Status)
            throw new InvalidOperationException("Estado terminal exige tentativa pendente.");
        if (status == StatusPagamento.Capturado && Status == StatusPagamento.SemTentativa)
            throw new InvalidOperationException("Captura exige tentativa iniciada.");
        if (status == StatusPagamento.Reembolsado && Status != StatusPagamento.Capturado)
            throw new InvalidOperationException("Reembolso externo exige pagamento capturado.");
        Status = status; ReferenciaProvedor = referencia;
        ResultadoPublicado = false; Revisao++;
        return true;
    }
    public void RegistrarCancelamentoDoOrcamento() => Revisao++;
    public void MarcarResultadoPublicado() { ResultadoPublicado = true; Revisao++; }
    public void DesfazerReembolso(Reembolso reembolso)
    {
        if (reembolso.Estado != EstadoReembolso.Pendente || !_reembolsos.Remove(reembolso)) return;
        Revisao++;
    }
    public void ConfirmarReembolso(Reembolso reembolso)
    {
        reembolso.Confirmar(); Revisao++;
        if (Status == StatusPagamento.Capturado && _reembolsos.Where(r => r.Estado == EstadoReembolso.Confirmado).Sum(r => r.Valor) == Valor) Status = StatusPagamento.Reembolsado;
    }
    public Reembolso? ObterReembolso(string referencia) => _reembolsos.FirstOrDefault(r => r.Referencia == referencia);
    public Reembolso Reembolsar(decimal valor, string aprovador, string referencia, DateTimeOffset quando)
    {
        if (Status != StatusPagamento.Capturado || valor <= 0 || decimal.Round(valor, 2) != valor ||
            string.IsNullOrWhiteSpace(aprovador) || string.IsNullOrWhiteSpace(referencia))
            throw new InvalidOperationException("Reembolso inválido.");
        if (valor > Valor) throw new ArgumentOutOfRangeException(nameof(valor), "Valor do reembolso excede o valor do pagamento.");
        var jaReembolsado = _reembolsos.Sum(r => r.Valor);
        if (valor + jaReembolsado > Valor || _reembolsos.Any(r => r.Referencia == referencia))
            throw new InvalidOperationException("Reembolso inválido.");
        var reembolso = new Reembolso(Id, valor, aprovador, referencia, quando); _reembolsos.Add(reembolso);
        Revisao++;
        return reembolso;
    }
}

public enum StatusPagamento { SemTentativa, Pendente, Capturado, Rejeitado, Cancelado, Expirado, Reembolsado }
