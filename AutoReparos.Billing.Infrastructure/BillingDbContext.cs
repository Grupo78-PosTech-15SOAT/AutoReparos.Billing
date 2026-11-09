using AutoReparos.Billing.Application;
using AutoReparos.Billing.Domain;
using Microsoft.EntityFrameworkCore;

namespace AutoReparos.Billing.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options), IBillingStore
{
    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<Reembolso> Reembolsos => Set<Reembolso>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Orcamento>().ToTable("orcamentos").HasKey(x => x.Id);
        model.Entity<Orcamento>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<Orcamento>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        model.Entity<Orcamento>().HasIndex(x => x.OrdemServicoId).IsUnique();
        model.Entity<Orcamento>().Property(x => x.Valor).HasPrecision(18, 2);
        model.Entity<Orcamento>().HasOne(x => x.Pagamento).WithOne().HasForeignKey<Pagamento>(x => x.OrcamentoId);
        model.Entity<Pagamento>().ToTable("pagamentos").HasKey(x => x.Id);
        model.Entity<Pagamento>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<Pagamento>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        model.Entity<Pagamento>().Property(x => x.Valor).HasPrecision(18, 2);
        model.Entity<Pagamento>().HasMany(x => x.Reembolsos).WithOne().HasForeignKey(x => x.PagamentoId);
        model.Entity<Reembolso>().ToTable("reembolsos").HasKey(x => x.Id);
        model.Entity<Reembolso>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<Reembolso>().Property(x => x.Valor).HasPrecision(18, 2);
        model.Entity<Reembolso>().HasIndex(x => new { x.PagamentoId, x.Referencia }).IsUnique();
    }
    public Task<Orcamento?> ObterOrcamentoAsync(Guid id, CancellationToken ct) => Orcamentos.Include(x => x.Pagamento).ThenInclude(x => x!.Reembolsos).SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Pagamento?> ObterPagamentoAsync(Guid id, CancellationToken ct) => Pagamentos.Include(x => x.Reembolsos).SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Orcamento?> ObterPorOrdemAsync(Guid ordemId, CancellationToken ct) => Orcamentos.Include(x => x.Pagamento).SingleOrDefaultAsync(x => x.OrdemServicoId == ordemId, ct);
    public Task<Orcamento?> ObterPorPagamentoAsync(Guid pagamentoId, CancellationToken ct) => Orcamentos.Include(x => x.Pagamento).SingleOrDefaultAsync(x => x.Pagamento!.Id == pagamentoId, ct);
    public Task AdicionarOrcamentoAsync(Orcamento orcamento, CancellationToken ct) => Orcamentos.AddAsync(orcamento, ct).AsTask();
    public async Task SalvarAsync(CancellationToken ct)
    {
        try { await SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex) { throw new ConcorrenciaException("Registro alterado por outra operação.", ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        { throw new ConcorrenciaException("Registro duplicado por outra operação.", ex); }
    }
}
