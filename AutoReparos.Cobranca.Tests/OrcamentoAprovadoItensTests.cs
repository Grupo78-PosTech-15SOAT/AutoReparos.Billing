using System.Text.Json;
using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;

namespace AutoReparos.Cobranca.Tests;

// Saga-compensacoes (D-32): o orcamento.aprovado.v1 leva os insumos do orçamento para o OS montar a reserva.
public sealed class OrcamentoAprovadoItensTests
{
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-000000000001");
    private static readonly Guid Filtro = Guid.Parse("1a5f0001-0000-4000-8000-000000000002");
    private static readonly Guid Alinhamento = Guid.Parse("5a2f0001-0000-4000-8000-000000000002");
    private static readonly DateTime Agora = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private static JsonElement Publicar(params ItemOrcamentoPreco[] itens)
    {
        var orcamento = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), itens, DateTimeOffset.UtcNow);
        orcamento.Aprovar();
        return JsonDocument.Parse(MensagensSaga.OrcamentoAprovado(orcamento, Agora).Payload).RootElement;
    }

    [Fact]
    public void OrcamentoAprovadoCarregaItens()
    {
        var corpo = Publicar(
            new(TipoItemOrcamento.Insumo, Oleo, "Óleo 5W30", 4, 42.50m),
            new(TipoItemOrcamento.Servico, Alinhamento, "Alinhamento", 1, 160m),
            new(TipoItemOrcamento.Insumo, Filtro, "Filtro", 1, 30m),
            new(TipoItemOrcamento.Insumo, Oleo, "Óleo 5W30", 1, 42.50m));

        Assert.Equal("orcamento.aprovado.v1", corpo.GetProperty("tipo").GetString());
        var itens = corpo.GetProperty("itens").EnumerateArray().Select(i => (i.GetProperty("insumoId").GetGuid(), i.GetProperty("quantidade").GetInt32())).ToList();
        Assert.Equal([(Oleo, 5), (Filtro, 1)], itens);
        foreach (var campo in new[] { "eventId", "ordemServicoId", "ocorreuEm", "orcamentoId", "clienteId" }) Assert.True(corpo.TryGetProperty(campo, out _), campo);
    }

    [Fact]
    public void OrcamentoSemInsumosPublicaItensVazios()
    {
        var corpo = Publicar(new ItemOrcamentoPreco(TipoItemOrcamento.Servico, Alinhamento, "Alinhamento", 1, 160m));
        Assert.Equal(JsonValueKind.Array, corpo.GetProperty("itens").ValueKind);
        Assert.Equal(0, corpo.GetProperty("itens").GetArrayLength());
    }

    [Fact]
    public void EventIdContinuaDeterministico()
    {
        var orcamento = new Orcamento(Guid.NewGuid(), Guid.NewGuid(), [new(TipoItemOrcamento.Insumo, Oleo, "Óleo", 1, 42.50m)], DateTimeOffset.UtcNow);
        orcamento.Aprovar();
        Assert.Equal(MensagensSaga.OrcamentoAprovado(orcamento, Agora).Id, MensagensSaga.OrcamentoAprovado(orcamento, Agora.AddMinutes(5)).Id);
    }
}
