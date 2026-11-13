using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Cobranca.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options), IBillingStore, IEventosProcessadosStore
{
    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    public DbSet<ItemOrcamento> ItensOrcamento => Set<ItemOrcamento>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<TokenAprovacao> TokensAprovacao => Set<TokenAprovacao>();
    public DbSet<Reembolso> Reembolsos => Set<Reembolso>();
    public DbSet<MensagemSaida> MensagensSaida => Set<MensagemSaida>();
    public DbSet<EventoProcessado> EventosProcessados => Set<EventoProcessado>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Orcamento>().ToTable("orcamentos").HasKey(x => x.Id);
        model.Entity<Orcamento>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<Orcamento>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        model.Entity<Orcamento>().HasIndex(x => x.OrdemServicoId).IsUnique();
        model.Entity<Orcamento>().Property(x => x.Valor).HasPrecision(18, 2);
        model.Entity<Orcamento>().HasOne(x => x.Pagamento).WithOne().HasForeignKey<Pagamento>(x => x.OrcamentoId);
        model.Entity<Orcamento>().HasMany(x => x.Itens).WithOne().HasForeignKey(x => x.OrcamentoId);
        model.Entity<Orcamento>().Navigation(x => x.Itens).UsePropertyAccessMode(PropertyAccessMode.Field);
        model.Entity<ItemOrcamento>().ToTable("itens_orcamento").HasKey(x => x.Id);
        model.Entity<ItemOrcamento>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<ItemOrcamento>().Property(x => x.Tipo).HasConversion<int>();
        model.Entity<ItemOrcamento>().Property(x => x.Descricao).HasMaxLength(LimitesItensOrcamento.DescricaoMaxima).IsRequired();
        model.Entity<ItemOrcamento>().Property(x => x.PrecoUnitario).HasPrecision(18, 2);
        model.Entity<ItemOrcamento>().Ignore(x => x.Subtotal);
        model.Entity<TokenAprovacao>().ToTable("tokens_aprovacao").HasKey(x => x.Id);
        model.Entity<TokenAprovacao>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<TokenAprovacao>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        model.Entity<TokenAprovacao>().Property(x => x.Hash).HasMaxLength(64).IsRequired();
        model.Entity<TokenAprovacao>().HasIndex(x => x.Hash).IsUnique();
        model.Entity<TokenAprovacao>().HasIndex(x => x.OrcamentoId).IsUnique();
        model.Entity<TokenAprovacao>().HasOne<Orcamento>().WithMany().HasForeignKey(x => x.OrcamentoId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Pagamento>().ToTable("pagamentos").HasKey(x => x.Id);
        model.Entity<Pagamento>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<Pagamento>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        model.Entity<Pagamento>().Property(x => x.Valor).HasPrecision(18, 2);
        model.Entity<Pagamento>().HasMany(x => x.Reembolsos).WithOne().HasForeignKey(x => x.PagamentoId);
        model.Entity<Reembolso>().ToTable("reembolsos").HasKey(x => x.Id);
        model.Entity<Reembolso>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<Reembolso>().Property(x => x.Valor).HasPrecision(18, 2);
        model.Entity<Reembolso>().Property(x => x.Estado).HasConversion<int>();
        model.Entity<Reembolso>().HasIndex(x => new { x.PagamentoId, x.Referencia }).IsUnique();
        model.Entity<MensagemSaida>().ToTable("mensagens_saida").HasKey(x => x.Id);
        model.Entity<MensagemSaida>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<MensagemSaida>().HasIndex(x => x.CriadoEm).HasFilter("\"EnviadoEm\" IS NULL AND \"EsgotadaEm\" IS NULL").HasDatabaseName("IX_mensagens_saida_pendentes");
        model.Entity<EventoProcessado>().ToTable("eventos_processados").HasKey(x => x.Id);
        model.Entity<EventoProcessado>().Property(x => x.Id).ValueGeneratedNever();
    }
    public Task<Orcamento?> ObterOrcamentoAsync(Guid id, CancellationToken ct) => Orcamentos.Include(x => x.Itens).Include(x => x.Pagamento).ThenInclude(x => x!.Reembolsos).SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Pagamento?> ObterPagamentoAsync(Guid id, CancellationToken ct) => Pagamentos.Include(x => x.Reembolsos).SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Orcamento?> ObterPorOrdemAsync(Guid ordemId, CancellationToken ct) => Orcamentos.Include(x => x.Itens).Include(x => x.Pagamento).SingleOrDefaultAsync(x => x.OrdemServicoId == ordemId, ct);
    public Task<Orcamento?> ObterPorPagamentoAsync(Guid pagamentoId, CancellationToken ct) => Orcamentos.Include(x => x.Pagamento).SingleOrDefaultAsync(x => x.Pagamento!.Id == pagamentoId, ct);
    public Task<TokenAprovacao?> ObterTokenPorHashAsync(string hash, CancellationToken ct) => TokensAprovacao.SingleOrDefaultAsync(x => x.Hash == hash, ct);
    public Task<TokenAprovacao?> ObterTokenDoOrcamentoAsync(Guid orcamentoId, CancellationToken ct) => TokensAprovacao.SingleOrDefaultAsync(x => x.OrcamentoId == orcamentoId, ct);
    public void AdicionarToken(TokenAprovacao token) => TokensAprovacao.Add(token);
    public Task AdicionarOrcamentoAsync(Orcamento orcamento, CancellationToken ct) => Orcamentos.AddAsync(orcamento, ct).AsTask();
    public void AdicionarMensagemSaida(MensagemSaida mensagem) => MensagensSaida.Add(mensagem);
    public Task<bool> ExisteAsync(Guid eventId, CancellationToken ct) => EventosProcessados.AnyAsync(x => x.Id == eventId, ct);
    public void Registrar(EventoProcessado evento) => EventosProcessados.Add(evento);
    public async Task SalvarAsync(CancellationToken ct)
    {
        try { await SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) { throw new ConcorrenciaException("Registro alterado por outra operação.", ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        { throw new ConcorrenciaException("Registro duplicado por outra operação.", ex); }
    }
}
