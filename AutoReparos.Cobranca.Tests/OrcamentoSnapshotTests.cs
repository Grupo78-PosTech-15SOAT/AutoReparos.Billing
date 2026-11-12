using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;
using AutoReparos.Cobranca.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace AutoReparos.Cobranca.Tests;

// Feature insumos: o orçamento grava o snapshot do PrecoUnitario lido do catálogo da Execução.
public sealed class OrcamentoSnapshotTests
{
    private static readonly Guid Alinhamento = Guid.Parse("5a2f0001-0000-4000-8000-000000000002");
    private static readonly Guid Oleo = Guid.Parse("1a5f0001-0000-4000-8000-000000000001");

    private sealed class CatalogoFalso : ICatalogoPrecos
    {
        public Dictionary<(TipoItemOrcamento, Guid), (string Nome, decimal Preco)> Precos { get; } = new()
        {
            [(TipoItemOrcamento.Servico, Alinhamento)] = ("Alinhamento", 160m),
            [(TipoItemOrcamento.Insumo, Oleo)] = ("Óleo 5W30", 42.50m)
        };
        public int Consultas { get; private set; }

        public Task<IReadOnlyList<ItemOrcamentoPreco>> ConsultarAsync(IReadOnlyList<ItemOrcamentoSolicitado> itens, CancellationToken ct)
        {
            Consultas++;
            var resultado = new List<ItemOrcamentoPreco>();
            foreach (var item in itens)
            {
                item.TentarTipo(out var tipo);
                if (!Precos.TryGetValue((tipo, item.ReferenciaId), out var p)) throw new ItemCatalogoInexistenteException(item.Tipo, item.ReferenciaId);
                resultado.Add(new ItemOrcamentoPreco(tipo, item.ReferenciaId, p.Nome, item.Quantidade, p.Preco));
            }
            return Task.FromResult<IReadOnlyList<ItemOrcamentoPreco>>(resultado);
        }
    }

    private sealed class Alertas : IAlertSink
    {
        public int Total { get; private set; }
        public Task AlertarAsync(string codigo, string detalhe, CancellationToken ct) { Total++; return Task.CompletedTask; }
    }

    private sealed class ManipuladorFalso(Func<HttpRequestMessage, int, HttpResponseMessage> resposta) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Chamadas { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Chamadas.Add(request);
            return Task.FromResult(resposta(request, Chamadas.Count));
        }
    }

    private sealed class AutenticacaoDeTeste(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var papel = Request.Headers["X-Test-Role"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(papel)) return Task.FromResult(AuthenticateResult.NoResult());
            var identidade = new ClaimsIdentity([new Claim(ClaimTypes.Role, papel)], Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), Scheme.Name)));
        }
    }

    private sealed class FabricaApi(string conexao, ICatalogoPrecos catalogo) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:1" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<BillingDbContext>>();
                services.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<BillingDbContext>));
                services.AddDbContext<BillingDbContext>(o => o.UseNpgsql(conexao));
                services.RemoveAll<ICatalogoPrecos>(); services.AddSingleton(catalogo);
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; options.DefaultScheme = "Test"; })
                    .AddScheme<AuthenticationSchemeOptions, AutenticacaoDeTeste>("Test", _ => { });
            });
        }
    }

    private static BillingDbContext NovoContexto(string conexao) => new(new DbContextOptionsBuilder<BillingDbContext>().UseNpgsql(conexao).Options);

    private static BillingService NovoServico(BillingDbContext db, ICatalogoPrecos catalogo) => new(db, null!, null!, null!, null!, catalogo);

    private static async Task<(PostgreSqlContainer Postgres, string Conexao)> IniciarBancoAsync()
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync();
        var conexao = postgres.GetConnectionString();
        await using var db = NovoContexto(conexao);
        await db.Database.MigrateAsync();
        return (postgres, conexao);
    }

    [Fact]
    public async Task OrcamentoGuardaSnapshotDePreco()
    {
        var (postgres, conexao) = await IniciarBancoAsync();
        await using var _ = postgres;
        var catalogo = new CatalogoFalso();
        Guid os = Guid.NewGuid(), cliente = Guid.NewGuid();
        var itens = new[] { new ItemOrcamentoSolicitado("servico", Alinhamento, 1), new ItemOrcamentoSolicitado("Insumo", Oleo, 2) };

        Guid orcamentoId;
        await using (var db = NovoContexto(conexao))
        {
            var criado = await NovoServico(db, catalogo).CriarOrcamentoAsync(new CriarOrcamento(os, cliente, Itens: itens), CancellationToken.None);
            orcamentoId = criado.Id;
            Assert.Equal(245.00m, criado.Valor);
        }

        // Reajuste do catálogo depois da criação e aprovação do orçamento.
        catalogo.Precos[(TipoItemOrcamento.Servico, Alinhamento)] = ("Alinhamento", 200m);
        catalogo.Precos[(TipoItemOrcamento.Insumo, Oleo)] = ("Óleo 5W30", 50m);
        await using (var db = NovoContexto(conexao)) await NovoServico(db, catalogo).AprovarOrcamentoAsync(orcamentoId, cliente, CancellationToken.None);

        await using (var db = NovoContexto(conexao))
        {
            var aprovado = await db.Orcamentos.AsNoTracking().Include(o => o.Itens).SingleAsync(o => o.Id == orcamentoId);
            Assert.Equal((StatusOrcamento.Aprovado, 245.00m), (aprovado.Status, aprovado.Valor));
            Assert.Equal([(TipoItemOrcamento.Servico, 1, 160m), (TipoItemOrcamento.Insumo, 2, 42.50m)], aprovado.Itens.OrderBy(i => i.Tipo).Select(i => (i.Tipo, i.Quantidade, i.PrecoUnitario)));
            Assert.Equal(["Alinhamento", "Óleo 5W30"], aprovado.Itens.OrderBy(i => i.Tipo).Select(i => i.Descricao));
            Assert.Equal(245.00m, aprovado.Itens.Sum(i => i.Subtotal));
            Assert.Equal(245.00m, (await db.Pagamentos.AsNoTracking().SingleAsync(p => p.OrcamentoId == orcamentoId)).Valor);
        }

        // Repetir a criação da mesma OS devolve o orçamento existente sem consultar o catálogo de novo.
        await using (var db = NovoContexto(conexao))
        {
            var consultasAntes = catalogo.Consultas;
            var repetido = await NovoServico(db, catalogo).CriarOrcamentoAsync(new CriarOrcamento(os, cliente, Itens: itens), CancellationToken.None);
            Assert.Equal((orcamentoId, 245.00m, consultasAntes), (repetido.Id, repetido.Valor, catalogo.Consultas));
        }

        // Um orçamento novo, de outra OS, já usa o preço reajustado.
        await using (var db = NovoContexto(conexao))
        {
            var novo = await NovoServico(db, catalogo).CriarOrcamentoAsync(new CriarOrcamento(Guid.NewGuid(), cliente, Itens: itens), CancellationToken.None);
            Assert.Equal(300m, novo.Valor);
        }
    }

    [Fact]
    public async Task OrcamentoComItemInexistenteNaoGravaNada()
    {
        var (postgres, conexao) = await IniciarBancoAsync();
        await using var _ = postgres;
        await using var db = NovoContexto(conexao);
        var desconhecido = Guid.NewGuid();

        var erro = await Assert.ThrowsAsync<ItemCatalogoInexistenteException>(() => NovoServico(db, new CatalogoFalso())
            .CriarOrcamentoAsync(new CriarOrcamento(Guid.NewGuid(), Guid.NewGuid(), Itens: [new ItemOrcamentoSolicitado("servico", Alinhamento, 1), new ItemOrcamentoSolicitado("insumo", desconhecido, 1)]), CancellationToken.None));

        Assert.Equal(desconhecido, erro.ReferenciaId);
        Assert.Equal(0, await db.Orcamentos.CountAsync());
        Assert.Equal(0, await db.ItensOrcamento.CountAsync());
    }

    [Fact]
    public async Task RotaDeOrcamentoAceitaItensEMantemOModoComValor()
    {
        var (postgres, conexao) = await IniciarBancoAsync();
        await using var _ = postgres;
        var catalogo = new CatalogoFalso();
        using var fabrica = new FabricaApi(conexao, catalogo);
        using var cliente = fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-Test-Role", "Atendente");
        Guid clienteId = Guid.NewGuid();

        var criado = await cliente.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId, itens = new[] { new { tipo = "servico", referenciaId = Alinhamento, quantidade = 1 }, new { tipo = "insumo", referenciaId = Oleo, quantidade = 2 } } });
        Assert.Equal(HttpStatusCode.Created, criado.StatusCode);
        var corpo = await criado.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(245.00m, corpo.GetProperty("valor").GetDecimal());
        Assert.Equal([160m, 42.50m], corpo.GetProperty("itens").EnumerateArray().Select(i => i.GetProperty("precoUnitario").GetDecimal()).OrderByDescending(p => p));

        // Modo antigo: valor direto, sem itens.
        var legado = await cliente.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId, valor = 250m });
        Assert.Equal(HttpStatusCode.Created, legado.StatusCode);
        Assert.Equal(0, (await legado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("itens").GetArrayLength());

        // Item inexistente, itens repetidos, quantidade inválida, tipo inválido e valor junto com itens: 422.
        var inexistente = await cliente.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId, itens = new[] { new { tipo = "insumo", referenciaId = Guid.NewGuid(), quantidade = 1 } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inexistente.StatusCode);
        Assert.Equal("item_catalogo_inexistente", (await inexistente.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        foreach (var invalido in new object[]
        {
            new { ordemServicoId = Guid.NewGuid(), clienteId, itens = new[] { new { tipo = "servico", referenciaId = Alinhamento, quantidade = 1 }, new { tipo = "SERVICO", referenciaId = Alinhamento, quantidade = 2 } } },
            new { ordemServicoId = Guid.NewGuid(), clienteId, itens = new[] { new { tipo = "servico", referenciaId = Alinhamento, quantidade = 0 } } },
            new { ordemServicoId = Guid.NewGuid(), clienteId, itens = new[] { new { tipo = "outro", referenciaId = Alinhamento, quantidade = 1 } } },
            new { ordemServicoId = Guid.NewGuid(), clienteId, valor = 10m, itens = new[] { new { tipo = "servico", referenciaId = Alinhamento, quantidade = 1 } } }
        })
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cliente.PostAsJsonAsync("/api/v1/orcamentos", invalido)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await cliente.PostAsJsonAsync("/api/v1/orcamentos", new { ordemServicoId = Guid.NewGuid(), clienteId, valor = 10m, itens = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task ClienteDoCatalogoLePrecosDaExecucao()
    {
        var servicoId = Guid.NewGuid();
        var manipulador = new ManipuladorFalso((req, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(req.RequestUri!.AbsolutePath.EndsWith("/servicos")
                ? $$"""{"itens":[{"id":"{{servicoId}}","nome":"Revisão","precoUnitario":220.00}]}"""
                : $$"""{"itens":[{"id":"{{Oleo}}","nome":"Óleo","precoUnitario":42.5}]}""", Encoding.UTF8, "application/json")
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Execucao:BaseUrl"] = "http://execucao.test/", ["Execucao:BearerToken"] = "token-servico" }).Build();
        var cliente = new CatalogoExecucaoClient(new HttpClient(manipulador), config, new Alertas(), _ => TimeSpan.Zero);

        var precos = await cliente.ConsultarAsync([new ItemOrcamentoSolicitado("insumo", Oleo, 3), new ItemOrcamentoSolicitado("servico", servicoId, 1)], CancellationToken.None);

        Assert.Equal([(TipoItemOrcamento.Insumo, "Óleo", 3, 42.5m), (TipoItemOrcamento.Servico, "Revisão", 1, 220.00m)], precos.Select(p => (p.Tipo, p.Descricao, p.Quantidade, p.PrecoUnitario)));
        Assert.Equal(["/api/v1/insumos", "/api/v1/servicos"], manipulador.Chamadas.Select(c => c.RequestUri!.AbsolutePath).Order());
        Assert.All(manipulador.Chamadas, c => Assert.Equal("Bearer token-servico", c.Headers.Authorization!.ToString()));
        await Assert.ThrowsAsync<ItemCatalogoInexistenteException>(() => cliente.ConsultarAsync([new ItemOrcamentoSolicitado("servico", Guid.NewGuid(), 1)], CancellationToken.None));
    }

    [Fact]
    public async Task ClienteDoCatalogoRepeteEAlertaQuandoAExecucaoCai()
    {
        var alertas = new Alertas();
        var manipulador = new ManipuladorFalso((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Execucao:BaseUrl"] = "http://execucao.test" }).Build();
        var cliente = new CatalogoExecucaoClient(new HttpClient(manipulador), config, alertas, _ => TimeSpan.Zero);

        var erro = await Assert.ThrowsAsync<DependenciaIndisponivelException>(() => cliente.ConsultarAsync([new ItemOrcamentoSolicitado("servico", Alinhamento, 1)], CancellationToken.None));

        Assert.Equal(("execucao", 5, 1), (erro.Dependencia, manipulador.Chamadas.Count, alertas.Total));
    }
}
