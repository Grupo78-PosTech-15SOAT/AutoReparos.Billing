using AutoReparos.Cobranca.Domain;
using AutoReparos.Cobranca.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoReparos.Cobranca.Tests;

// Feature web-adaptacao: leituras de orçamento e pagamento, link de aprovação por token e rotas públicas (PostgreSQL real).
public sealed partial class BillingLifecycleTests
{
    private static readonly Guid ServicoWeb = Guid.Parse("5a2f0001-0000-4000-8000-0000000000a1");
    private static readonly Guid InsumoWeb = Guid.Parse("1a5f0001-0000-4000-8000-0000000000a2");

    private sealed class CapturaDeLog : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _linhas = new();
        public string Tudo => string.Join('\n', _linhas);
        public ILogger CreateLogger(string categoryName) => new Registro(categoryName, _linhas);
        public void Dispose() { }
        private sealed class Registro(string categoria, ConcurrentQueue<string> linhas) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { linhas.Enqueue($"{categoria} scope {state}"); return null; }
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                linhas.Enqueue($"{categoria} {logLevel} {formatter(state, exception)} {exception}");
        }
    }

    private static async Task<Orcamento> SeedComItens(MensageriaFactory factory, bool approved = false)
    {
        var budget = new Orcamento(Guid.NewGuid(), Guid.NewGuid(),
            [new ItemOrcamentoPreco(TipoItemOrcamento.Servico, ServicoWeb, "Alinhamento", 1, 160m), new ItemOrcamentoPreco(TipoItemOrcamento.Insumo, InsumoWeb, "Óleo 5W30", 2, 42.50m)], DateTimeOffset.UtcNow);
        if (approved) budget.Aprovar();
        await Db(factory, async db => { db.Orcamentos.Add(budget); await db.SaveChangesAsync(); return 0; });
        return budget;
    }

    private static HttpClient Anonimo(MensageriaFactory factory) => factory.CreateClient();

    private static async Task<string> GerarLink(HttpClient atendente, Guid orcamentoId)
    {
        var resposta = await atendente.PostAsync($"/api/v1/orcamentos/{orcamentoId}/link-aprovacao", null);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static async Task<string> Codigo(HttpResponseMessage resposta) => (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString()!;
    private static Task<HttpResponseMessage> Aprovar(HttpClient client, string? token) => client.PostAsJsonAsync("/api/v1/orcamentos/publico/aprovacao", new { token });
    private static Task<HttpResponseMessage> Resumo(HttpClient client, string token) => client.GetAsync($"/api/v1/orcamentos/publico?token={Uri.EscapeDataString(token)}");

    [Fact]
    public async Task ConsultaOrcamentoPorOs()
    {
        var (factory, postgres, _, _, _, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedComItens(factory);
        using var atendente = Authorized(factory.CreateClient(), "Atendente"); using var admin = Authorized(factory.CreateClient());

        var resposta = await atendente.GetAsync($"/api/v1/orcamentos?ordemServicoId={budget.OrdemServicoId}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((budget.Id, 245.00m, "Pendente"), (corpo.GetProperty("id").GetGuid(), corpo.GetProperty("valor").GetDecimal(), corpo.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, corpo.GetProperty("pagamentoId").ValueKind);
        Assert.Equal([("Servico", "Alinhamento", 1, 160m, 160m), ("Insumo", "Óleo 5W30", 2, 42.50m, 85m)],
            corpo.GetProperty("itens").EnumerateArray().Select(i => (i.GetProperty("tipo").GetString()!, i.GetProperty("descricao").GetString()!, i.GetProperty("quantidade").GetInt32(), i.GetProperty("precoUnitario").GetDecimal(), i.GetProperty("subtotal").GetDecimal())).OrderBy(i => i.Item1 == "Insumo"));

        // Depois da aprovação o orçamento aponta para o pagamento; o administrador também consulta.
        var aprovado = await SeedComItens(factory, approved: true);
        var doAprovado = await (await admin.GetAsync($"/api/v1/orcamentos?ordemServicoId={aprovado.OrdemServicoId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((aprovado.Pagamento!.Id, "Aprovado"), (doAprovado.GetProperty("pagamentoId").GetGuid(), doAprovado.GetProperty("status").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await atendente.GetAsync($"/api/v1/orcamentos?ordemServicoId={Guid.NewGuid()}")).StatusCode);
        foreach (var url in new[] { "/api/v1/orcamentos", "/api/v1/orcamentos?ordemServicoId=", "/api/v1/orcamentos?ordemServicoId=abc", $"/api/v1/orcamentos?ordemServicoId={Guid.Empty}" })
        {
            var invalida = await atendente.GetAsync(url);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalida.StatusCode);
            Assert.Equal("ordem_servico_invalida", await Codigo(invalida));
        }
        var rota = $"/api/v1/orcamentos?ordemServicoId={budget.OrdemServicoId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonimo(factory).GetAsync(rota)).StatusCode);
        foreach (var papel in new[] { "Cliente", "Mecanico", "ServicoOS" })
        {
            using var proibido = Authorized(factory.CreateClient(), papel);
            Assert.Equal(HttpStatusCode.Forbidden, (await proibido.GetAsync(rota)).StatusCode);
        }
    }

    [Fact]
    public async Task ConsultaPagamentoPorId()
    {
        var (factory, postgres, _, _, _, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var semCheckout = await SeedBudget(factory, approved: true); var comCheckout = await SeedBudget(factory, checkoutStarted: true);
        using var atendente = Authorized(factory.CreateClient(), "Atendente"); using var admin = Authorized(factory.CreateClient());

        var sem = await (await atendente.GetAsync($"/api/v1/pagamentos/{semCheckout.Pagamento!.Id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((semCheckout.Pagamento.Id, "SemTentativa", 250m, JsonValueKind.Null), (sem.GetProperty("id").GetGuid(), sem.GetProperty("status").GetString(), sem.GetProperty("valor").GetDecimal(), sem.GetProperty("checkoutUrl").ValueKind));
        var com = await (await admin.GetAsync($"/api/v1/pagamentos/{comCheckout.Pagamento!.Id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Pendente", "https://checkout.example/1"), (com.GetProperty("status").GetString(), com.GetProperty("checkoutUrl").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await atendente.GetAsync($"/api/v1/pagamentos/{Guid.NewGuid()}")).StatusCode);
        var rota = $"/api/v1/pagamentos/{comCheckout.Pagamento.Id}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonimo(factory).GetAsync(rota)).StatusCode);
        foreach (var papel in new[] { "Cliente", "Mecanico", "ServicoOS" })
        {
            using var proibido = Authorized(factory.CreateClient(), papel);
            Assert.Equal(HttpStatusCode.Forbidden, (await proibido.GetAsync(rota)).StatusCode);
        }
    }

    [Fact]
    public async Task LinkDeAprovacaoGuardaSoOHash()
    {
        var (factory, postgres, _, _, _, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedComItens(factory);
        using var atendente = Authorized(factory.CreateClient(), "Atendente"); var anonimo = Anonimo(factory);

        var antes = DateTimeOffset.UtcNow;
        var resposta = await atendente.PostAsync($"/api/v1/orcamentos/{budget.Id}/link-aprovacao", null);
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        var primeiro = corpo.GetProperty("token").GetString()!;
        Assert.True(Convert.FromBase64String(primeiro.Replace('-', '+').Replace('_', '/') + "=").Length >= 16);
        Assert.InRange(corpo.GetProperty("expiraEm").GetDateTimeOffset() - antes, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));

        // O banco guarda só o hash SHA-256; o token em claro não está em nenhuma coluna de nenhuma tabela.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(primeiro)));
        var registro = await Db(factory, db => db.TokensAprovacao.AsNoTracking().SingleAsync());
        Assert.Equal((budget.Id, hash), (registro.OrcamentoId, registro.Hash)); Assert.Null(registro.UsadoEm);
        Assert.DoesNotContain(primeiro, await TudoNoBanco(factory));

        // Novo pedido invalida o anterior.
        var segundo = await GerarLink(atendente, budget.Id);
        Assert.NotEqual(primeiro, segundo);
        Assert.Equal(1, await Db(factory, db => db.TokensAprovacao.CountAsync()));
        Assert.DoesNotContain(segundo, await TudoNoBanco(factory));
        var antigo = await Resumo(anonimo, primeiro);
        Assert.Equal((HttpStatusCode.NotFound, "token_invalido"), (antigo.StatusCode, await Codigo(antigo)));
        Assert.Equal(HttpStatusCode.OK, (await Resumo(anonimo, segundo)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Aprovar(anonimo, primeiro)).StatusCode);

        // Papéis, orçamento inexistente e orçamento fora de Pendente.
        var rota = $"/api/v1/orcamentos/{budget.Id}/link-aprovacao";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync(rota, null)).StatusCode);
        foreach (var papel in new[] { "Cliente", "Mecanico", "ServicoOS" })
        {
            using var proibido = Authorized(factory.CreateClient(), papel);
            Assert.Equal(HttpStatusCode.Forbidden, (await proibido.PostAsync(rota, null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await atendente.PostAsync($"/api/v1/orcamentos/{Guid.NewGuid()}/link-aprovacao", null)).StatusCode);
        var aprovado = await SeedComItens(factory, approved: true);
        var conflito = await atendente.PostAsync($"/api/v1/orcamentos/{aprovado.Id}/link-aprovacao", null);
        Assert.Equal((HttpStatusCode.Conflict, "orcamento_nao_pendente"), (conflito.StatusCode, await Codigo(conflito)));
    }

    private static Task<string> TudoNoBanco(MensageriaFactory factory) => Db(factory, async db =>
    {
        var linhas = new List<string>();
        var conexao = db.Database.GetDbConnection(); await conexao.OpenAsync();
        try
        {
            foreach (var tabela in new[] { "orcamentos", "itens_orcamento", "pagamentos", "tokens_aprovacao", "mensagens_saida" })
            {
                await using var comando = conexao.CreateCommand(); comando.CommandText = $"select t::text from {tabela} t";
                await using var leitor = await comando.ExecuteReaderAsync();
                while (await leitor.ReadAsync()) linhas.Add(leitor.GetString(0));
            }
        }
        finally { await conexao.CloseAsync(); }
        return string.Join('\n', linhas);
    });

    [Fact]
    public async Task ResumoPublicoPorToken()
    {
        var (factory, postgres, _, _, _, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedComItens(factory);
        using var atendente = Authorized(factory.CreateClient(), "Atendente"); var anonimo = Anonimo(factory);
        var token = await GerarLink(atendente, budget.Id);

        var ok = await Resumo(anonimo, token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var corpo = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((245.00m, "Pendente"), (corpo.GetProperty("valor").GetDecimal(), corpo.GetProperty("status").GetString()));
        Assert.Equal(["Alinhamento", "Óleo 5W30"], corpo.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("descricao").GetString()!).Order());
        // O resumo público não expõe identificadores do cliente nem da ordem de serviço.
        var texto = corpo.GetRawText();
        Assert.DoesNotContain(budget.ClienteId.ToString(), texto); Assert.DoesNotContain(budget.OrdemServicoId.ToString(), texto);

        // Desconhecido, malformado, ausente, expirado e usado dão a mesma resposta 404.
        var esperado = (HttpStatusCode.NotFound, "{\"codigo\":\"token_invalido\"}");
        var desconhecido = TokenAprovacao.GerarToken();
        foreach (var invalido in new[] { desconhecido, "curto", new string('A', 43) + "!", "%00" })
        {
            var r = await Resumo(anonimo, invalido);
            Assert.Equal(esperado, (r.StatusCode, await r.Content.ReadAsStringAsync()));
        }
        var ausente = await anonimo.GetAsync("/api/v1/orcamentos/publico");
        Assert.Equal(esperado, (ausente.StatusCode, await ausente.Content.ReadAsStringAsync()));

        await Db(factory, db => db.TokensAprovacao.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiraEm, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var expirado = await Resumo(anonimo, token);
        Assert.Equal(esperado, (expirado.StatusCode, await expirado.Content.ReadAsStringAsync()));

        var novo = await GerarLink(atendente, budget.Id);
        Assert.Equal(HttpStatusCode.OK, (await Aprovar(anonimo, novo)).StatusCode);
        var usado = await Resumo(anonimo, novo);
        Assert.Equal(esperado, (usado.StatusCode, await usado.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task AprovacaoPublicaGravaOutboxEInvalidaToken()
    {
        var (factory, postgres, _, _, sqs, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedComItens(factory);
        using var atendente = Authorized(factory.CreateClient(), "Atendente"); var anonimo = Anonimo(factory);
        var token = await GerarLink(atendente, budget.Id);

        var resposta = await Aprovar(anonimo, token);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("Aprovado", (await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var salvo = await Db(factory, db => db.Orcamentos.AsNoTracking().Include(o => o.Pagamento).SingleAsync(o => o.Id == budget.Id));
        Assert.Equal((StatusOrcamento.Aprovado, 245.00m), (salvo.Status, salvo.Pagamento!.Valor));
        Assert.NotNull((await Db(factory, db => db.TokensAprovacao.AsNoTracking().SingleAsync())).UsadoEm);
        var mensagem = Assert.Single(await Outbox(factory));
        Assert.Equal("orcamento.aprovado.v1", mensagem.Tipo);
        var envelope = Parse(mensagem.Payload);
        Assert.Equal((budget.Id, budget.OrdemServicoId, budget.ClienteId), (envelope.GetProperty("orcamentoId").GetGuid(), envelope.GetProperty("ordemServicoId").GetGuid(), envelope.GetProperty("clienteId").GetGuid()));
        Assert.Equal(1, await DispatchAsync(factory)); Assert.Single(sqs.Delivered("main"));

        // O token deixou de valer: repetir dá 409 e não grava outra mensagem; o resumo some.
        var repetido = await Aprovar(anonimo, token);
        Assert.Equal((HttpStatusCode.Conflict, "orcamento_nao_pendente"), (repetido.StatusCode, await Codigo(repetido)));
        Assert.Single(await Outbox(factory));
        Assert.Equal(HttpStatusCode.NotFound, (await Resumo(anonimo, token)).StatusCode);

        // Token desconhecido, malformado, sem token e corpo inválido: 404 token_invalido.
        foreach (var invalido in new string?[] { TokenAprovacao.GerarToken(), "curto", null })
        {
            var r = await Aprovar(anonimo, invalido);
            Assert.Equal((HttpStatusCode.NotFound, "token_invalido"), (r.StatusCode, await Codigo(r)));
        }
        var semCorpo = await anonimo.PostAsync("/api/v1/orcamentos/publico/aprovacao", new StringContent("nao e json", Encoding.UTF8, "application/json"));
        Assert.Equal((HttpStatusCode.NotFound, "token_invalido"), (semCorpo.StatusCode, await Codigo(semCorpo)));

        // Expirado: 404, e o orçamento segue Pendente.
        var outro = await SeedComItens(factory);
        var tokenExpirado = await GerarLink(atendente, outro.Id);
        await Db(factory, db => db.TokensAprovacao.Where(t => t.OrcamentoId == outro.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiraEm, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(HttpStatusCode.NotFound, (await Aprovar(anonimo, tokenExpirado)).StatusCode);
        Assert.Equal(StatusOrcamento.Pendente, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == outro.Id))).Status);

        // Orçamento cancelado depois do link: token válido, mas fora de Pendente -> 409.
        var cancelado = await SeedComItens(factory);
        var tokenCancelado = await GerarLink(atendente, cancelado.Id);
        await Db(factory, db => db.Orcamentos.Where(o => o.Id == cancelado.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StatusOrcamento.Cancelado)));
        var recusado = await Aprovar(anonimo, tokenCancelado);
        Assert.Equal((HttpStatusCode.Conflict, "orcamento_nao_pendente"), (recusado.StatusCode, await Codigo(recusado)));
        Assert.Single(await Outbox(factory));
    }

    [Fact]
    public async Task AprovacaoPublicaConcorrenteAprovaUmaVez()
    {
        var (factory, postgres, gate, _, sqs, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var budget = await SeedComItens(factory);
        using var atendente = Authorized(factory.CreateClient(), "Atendente");
        var token = await GerarLink(atendente, budget.Id);
        using var a = Anonimo(factory); using var b = Anonimo(factory);

        gate.Arm(2);
        var respostas = await Task.WhenAll(Aprovar(a, token), Aprovar(b, token));
        gate.Arm(0);

        Assert.Equal([200, 409], respostas.Select(r => (int)r.StatusCode).Order().ToArray());
        Assert.Equal("orcamento_nao_pendente", await Codigo(respostas.Single(r => r.StatusCode == HttpStatusCode.Conflict)));
        Assert.Equal("orcamento.aprovado.v1", Assert.Single(await Outbox(factory)).Tipo);
        Assert.Equal(1, await DispatchAsync(factory)); Assert.Single(sqs.Delivered("main"));
        Assert.Equal(StatusOrcamento.Aprovado, (await Db(factory, db => db.Orcamentos.AsNoTracking().SingleAsync(o => o.Id == budget.Id))).Status);
    }

    [Fact]
    public async Task TokenNaoVazaNoLogNemNoErro()
    {
        var (factory, postgres, _, _, _, _, _, _) = await StartMensageriaAsync();
        await using var _p = postgres; using var _f = factory;
        var captura = new CapturaDeLog();
        using var comLog = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(captura))));
        var budget = await SeedComItens(factory);
        using var atendente = Authorized(comLog.CreateClient(), "Atendente"); var anonimo = comLog.CreateClient();
        var valido = await GerarLink(atendente, budget.Id);
        var desconhecido = TokenAprovacao.GerarToken(); var expirado = TokenAprovacao.GerarToken(); var malformado = "malformado-" + TokenAprovacao.GerarToken();

        var corpos = new List<string>();
        foreach (var token in new[] { valido, desconhecido, malformado })
        {
            var r = await Resumo(anonimo, token); corpos.Add(await r.Content.ReadAsStringAsync());
            r = await Aprovar(anonimo, token == valido ? desconhecido : token); corpos.Add(await r.Content.ReadAsStringAsync());
        }
        // Falhas de estado também não repetem o token: aprovado e repetido.
        Assert.Equal(HttpStatusCode.OK, (await Aprovar(anonimo, valido)).StatusCode);
        var repetido = await Aprovar(anonimo, valido); corpos.Add(await repetido.Content.ReadAsStringAsync());
        var novoOrcamento = await SeedComItens(factory);
        var tokenExpirado = await GerarLink(atendente, novoOrcamento.Id);
        await Db(factory, db => db.TokensAprovacao.Where(t => t.OrcamentoId == novoOrcamento.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiraEm, DateTimeOffset.UtcNow.AddMinutes(-1))));
        corpos.Add(await (await Resumo(anonimo, tokenExpirado)).Content.ReadAsStringAsync());
        corpos.Add(await (await Aprovar(anonimo, tokenExpirado)).Content.ReadAsStringAsync());

        var log = captura.Tudo;
        Assert.NotEmpty(log);
        foreach (var token in new[] { valido, desconhecido, malformado, tokenExpirado, expirado })
        {
            Assert.DoesNotContain(token, log);
            Assert.All(corpos, corpo => Assert.DoesNotContain(token, corpo));
        }
        Assert.DoesNotContain(TokenAprovacao.CalcularHash(valido), log);
        // Inexistente, malformado e expirado não se distinguem.
        Assert.All(corpos.Where(c => c.Contains("token_invalido")), c => Assert.Equal("{\"codigo\":\"token_invalido\"}", c));
    }
}
