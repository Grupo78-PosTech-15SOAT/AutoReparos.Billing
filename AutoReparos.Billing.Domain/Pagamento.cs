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
    public ICollection<Reembolso> Reembolsos => _reembolsos;
    public void IniciarCheckout(string url, string referencia)
    {
        if (Status is StatusPagamento.Pendente or StatusPagamento.Capturado or StatusPagamento.Reembolsado) throw new InvalidOperationException("Já existe tentativa ativa, pagamento capturado ou reembolsado.");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(referencia)) throw new ArgumentException("Checkout e referência do provedor são obrigatórios.");
        UrlCheckout = url; ReferenciaProvedor = referencia; Status = StatusPagamento.Pendente;
    }
    public void AplicarEstado(StatusPagamento status, string referencia)
    {
        if (Status == StatusPagamento.Capturado && status != StatusPagamento.Capturado) throw new InvalidOperationException("Pagamento capturado é terminal.");
        if (Status == StatusPagamento.Reembolsado && status != StatusPagamento.Reembolsado) throw new InvalidOperationException("Pagamento reembolsado é terminal.");
        if (status is StatusPagamento.Rejeitado or StatusPagamento.Cancelado or StatusPagamento.Expirado && Status != StatusPagamento.Pendente && status != Status)
            throw new InvalidOperationException("Estado terminal exige tentativa pendente.");
        if (status == StatusPagamento.Capturado && Status == StatusPagamento.SemTentativa)
            throw new InvalidOperationException("Captura exige tentativa iniciada.");
        if (status == StatusPagamento.Reembolsado && Status != StatusPagamento.Capturado)
            throw new InvalidOperationException("Reembolso externo exige pagamento capturado.");
        Status = status; ReferenciaProvedor = referencia;
        ResultadoPublicado = false;
    }
    public void MarcarResultadoPublicado() => ResultadoPublicado = true;
    public Reembolso Reembolsar(decimal valor, string aprovador, string referencia, DateTimeOffset quando)
    {
        var jaReembolsado = _reembolsos.Sum(r => r.Valor);
        if (Status != StatusPagamento.Capturado || valor <= 0 || decimal.Round(valor, 2) != valor || valor + jaReembolsado > Valor || string.IsNullOrWhiteSpace(aprovador) || string.IsNullOrWhiteSpace(referencia))
            throw new InvalidOperationException("Reembolso inválido.");
        var reembolso = new Reembolso(Id, valor, aprovador, referencia, quando); _reembolsos.Add(reembolso);
        if (valor + jaReembolsado == Valor) Status = StatusPagamento.Reembolsado;
        return reembolso;
    }
}

public enum StatusPagamento { SemTentativa, Pendente, Capturado, Rejeitado, Cancelado, Expirado, Reembolsado }
