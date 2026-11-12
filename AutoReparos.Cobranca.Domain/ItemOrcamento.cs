namespace AutoReparos.Cobranca.Domain;

public enum TipoItemOrcamento { Servico = 0, Insumo = 1 }

// Preço lido do catálogo da Execução no momento de montar o orçamento.
public sealed record ItemOrcamentoPreco(TipoItemOrcamento Tipo, Guid ReferenciaId, string Descricao, int Quantidade, decimal PrecoUnitario);

public static class LimitesItensOrcamento
{
    public const int ItensMaximos = 50;
    public const int QuantidadeMaxima = 1_000;
    public const int DescricaoMaxima = 100;
}

// Snapshot de um item do orçamento: o `PrecoUnitario` é gravado aqui e nunca relido do catálogo, então reajustes posteriores não alteram orçamentos já criados.
public sealed class ItemOrcamento
{
    private ItemOrcamento() { }
    internal ItemOrcamento(Guid orcamentoId, ItemOrcamentoPreco item)
    {
        Validar(item);
        Id = Guid.NewGuid(); OrcamentoId = orcamentoId; Tipo = item.Tipo; ReferenciaId = item.ReferenciaId;
        Descricao = item.Descricao.Trim(); Quantidade = item.Quantidade; PrecoUnitario = item.PrecoUnitario;
    }
    public Guid Id { get; private set; }
    public Guid OrcamentoId { get; private set; }
    public TipoItemOrcamento Tipo { get; private set; }
    public Guid ReferenciaId { get; private set; }
    public string Descricao { get; private set; } = string.Empty;
    public int Quantidade { get; private set; }
    public decimal PrecoUnitario { get; private set; }
    public decimal Subtotal => Quantidade * PrecoUnitario;

    internal static void Validar(ItemOrcamentoPreco item)
    {
        if (!Enum.IsDefined(item.Tipo) || item.ReferenciaId == Guid.Empty || string.IsNullOrWhiteSpace(item.Descricao) || item.Descricao.Trim().Length > LimitesItensOrcamento.DescricaoMaxima
            || item.Quantidade is < 1 or > LimitesItensOrcamento.QuantidadeMaxima || item.PrecoUnitario <= 0 || decimal.Round(item.PrecoUnitario, 2) != item.PrecoUnitario)
            throw new ArgumentException("Item de orçamento inválido.");
    }
}
