namespace AutoReparos.Billing.Domain;

public sealed class Reembolso(Guid pagamentoId, decimal valor, string aprovador, string referencia, DateTimeOffset criadoEm)
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PagamentoId { get; private set; } = pagamentoId;
    public decimal Valor { get; private set; } = valor;
    public string Aprovador { get; private set; } = aprovador;
    public string Referencia { get; private set; } = referencia;
    public DateTimeOffset CriadoEm { get; private set; } = criadoEm;
}
