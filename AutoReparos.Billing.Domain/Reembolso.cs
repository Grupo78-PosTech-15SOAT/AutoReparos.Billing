namespace AutoReparos.Billing.Domain;

public sealed class Reembolso(Guid pagamentoId, decimal valor, string aprovador, string referencia, DateTimeOffset criadoEm)
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PagamentoId { get; private set; } = pagamentoId;
    public decimal Valor { get; private set; } = valor;
    public string Aprovador { get; private set; } = aprovador;
    public string Referencia { get; private set; } = referencia;
    public DateTimeOffset CriadoEm { get; private set; } = criadoEm;
    public EstadoReembolso Estado { get; private set; } = EstadoReembolso.Pendente;
    public void Confirmar() => Estado = EstadoReembolso.Confirmado;
}

public enum EstadoReembolso { Pendente, Confirmado }

public static class LimitesBilling
{
    /// <summary>Maior valor suportado por numeric(18,2).</summary>
    public const decimal ValorMaximo = 9_999_999_999_999_999.99m;
}
