using Amazon.SQS;
using AutoReparos.Cobranca.Api;
using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;
using AutoReparos.Cobranca.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "autoreparos-billing";
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"] ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => TelemetriaBilling.Configurar(resource, serviceName, builder.Environment.EnvironmentName))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)));
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
});
builder.Services.AddOpenApi();
builder.Services.AddDbContext<BillingDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Billing")));
builder.Services.AddScoped<IBillingStore>(sp => sp.GetRequiredService<BillingDbContext>());
builder.Services.AddScoped<BillingService>();
builder.Services.AddHttpClient<ICheckoutProvider, MercadoPagoClient>();
builder.Services.AddHttpClient<IOrderReadiness, OsReadinessClient>();
builder.Services.AddHttpClient<ICatalogoPrecos, CatalogoExecucaoClient>();
builder.Services.AddSingleton<IWebhookSignatureValidator, MercadoPagoSignatureValidator>();
builder.Services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
builder.Services.AddScoped<IAlertSink, LoggingAlertSink>();
builder.Services.AddScoped<IResultadoPublisher, SqsResultPublisher>();
builder.Services.AddScoped<IEventosProcessadosStore>(sp => sp.GetRequiredService<BillingDbContext>());
builder.Services.AddScoped<CancelamentoOrcamentoService>();
builder.Services.AddScoped<OutboxProcessor>();
builder.Services.AddScoped<ReprocessadorDlq>();
builder.Services.AddSingleton<ProcessadorComandos>();
// Em testes (ambiente Testing) os serviços em segundo plano não sobem: cada teste os cria sobre o seu banco e o seu SQS falso.
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<OutboxDispatcher>();
    builder.Services.AddHostedService<ComandosConsumer>();
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = builder.Configuration["Authentication:Audience"];
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ServicoOS", p => p.RequireRole("ServicoOS"));
    options.AddPolicy("OperarBilling", p => p.RequireRole("Administrador", "Atendente", "ServicoOS"));
    options.AddPolicy("AprovarReembolso", p => p.RequireRole("Administrador"));
    options.AddPolicy("AdministrarDlq", p => p.RequireRole("Administrador"));
    options.AddPolicy("AprovarOrcamento", p => p.RequireRole("Cliente"));
    options.AddPolicy("IniciarCheckout", p => p.RequireRole("Administrador", "Atendente", "Mecanico", "ServicoOS", "Cliente"));
});

var app = builder.Build();
if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
}
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (DependenciaIndisponivelException ex) when (!context.Response.HasStarted)
    { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; await context.Response.WriteAsJsonAsync(new { codigo = "dependencia_indisponivel", dependencia = ex.Dependencia }); }
    catch (RespostaProvedorInvalidaException ex) when (!context.Response.HasStarted)
    { context.Response.StatusCode = StatusCodes.Status502BadGateway; await context.Response.WriteAsJsonAsync(new { codigo = "resposta_provedor_invalida", provedor = ex.Provedor }); }
    catch (ConcorrenciaException) when (!context.Response.HasStarted)
    { context.Response.StatusCode = StatusCodes.Status409Conflict; await context.Response.WriteAsJsonAsync(new { codigo = "conflito_concorrencia" }); }
});
app.UseAuthentication(); app.UseAuthorization();

var orcamentos = app.MapGroup("/api/v1/orcamentos").RequireAuthorization("OperarBilling");
orcamentos.MapPost("", async (CriarOrcamento request, BillingService service, CancellationToken ct) =>
{
    var comItens = request.Itens is { Count: > 0 };
    if (request.OrdemServicoId == Guid.Empty || request.ClienteId == Guid.Empty
        || (comItens ? request.Valor != 0 || !ItemOrcamentoSolicitado.ListaValida(request.Itens!)
            : request.Itens is not null || request.Valor <= 0 || request.Valor > LimitesBilling.ValorMaximo || decimal.Round(request.Valor, 2) != request.Valor))
        return Results.UnprocessableEntity(new { codigo = "orcamento_invalido" });
    Orcamento result;
    try { result = await service.CriarOrcamentoAsync(request, ct); }
    catch (ItemCatalogoInexistenteException ex) { return Results.UnprocessableEntity(new { codigo = "item_catalogo_inexistente", tipo = ex.Tipo, referenciaId = ex.ReferenciaId }); }
    catch (ArgumentException) { return Results.UnprocessableEntity(new { codigo = "orcamento_invalido" }); }
    return Results.Created($"/api/v1/orcamentos/{result.Id}", new
    {
        result.Id,
        result.OrdemServicoId,
        result.ClienteId,
        result.Valor,
        status = result.Status.ToString(),
        approvalUrl = $"/api/v1/orcamentos/{result.Id}/aprovacao",
        itens = result.Itens.Select(i => new { tipo = i.Tipo.ToString(), i.ReferenciaId, i.Descricao, i.Quantidade, i.PrecoUnitario, i.Subtotal })
    });
});
app.MapPost("/api/v1/orcamentos/{id:guid}/aprovacao", async (Guid id, HttpContext context, BillingService service, CancellationToken ct) =>
{
    if (!Guid.TryParse(context.User.FindFirst("clienteId")?.Value, out var clienteId)) return Results.Forbid();
    try { var result = await service.AprovarOrcamentoAsync(id, clienteId, ct); return Results.Ok(new { result.Id, status = result.Status.ToString(), pagamentoId = result.Pagamento?.Id }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { codigo = "transicao_invalida", detalhe = ex.Message }); }
}).RequireAuthorization("AprovarOrcamento");

var pagamentos = app.MapGroup("/api/v1/pagamentos");
pagamentos.MapPost("/{id:guid}/checkout", async (Guid id, HttpContext context, BillingService service, CancellationToken ct) =>
{
    Guid? clienteId = null;
    if (context.User.IsInRole("Cliente"))
    { if (!Guid.TryParse(context.User.FindFirst("clienteId")?.Value, out var parsed)) return Results.Forbid(); clienteId = parsed; }
    try { var p = await service.AbrirCheckoutAsync(id, clienteId, ct); return Results.Ok(new { p.Id, p.UrlCheckout, status = p.Status.ToString() }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { codigo = "checkout_indisponivel", detalhe = ex.Message }); }
}).RequireAuthorization("IniciarCheckout");
pagamentos.MapPost("/{id:guid}/reembolsos", async (Guid id, SolicitacaoReembolso request, HttpContext context, BillingService service, CancellationToken ct) =>
{
    var aprovador = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "";
    if (request.Valor <= 0 || request.Valor > LimitesBilling.ValorMaximo || decimal.Round(request.Valor, 2) != request.Valor || string.IsNullOrWhiteSpace(request.Referencia) || string.IsNullOrWhiteSpace(aprovador)) return Results.UnprocessableEntity(new { codigo = "reembolso_invalido" });
    try { var r = await service.ReembolsarAsync(id, request.Valor, aprovador, request.Referencia, ct); return Results.Created($"/api/v1/pagamentos/{id}/reembolsos/{r.Id}", new { r.Id, r.PagamentoId, r.Valor, r.Aprovador, r.Referencia }); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException) { return Results.UnprocessableEntity(new { codigo = "reembolso_invalido" }); }
    catch (HttpRequestException) { return Results.Json(new { codigo = "reembolso_recusado_provedor" }, statusCode: StatusCodes.Status502BadGateway); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { codigo = "reembolso_indisponivel", detalhe = ex.Message }); }
}).RequireAuthorization("AprovarReembolso");

app.MapPost("/api/v1/webhooks/mercado-pago", async (HttpRequest request, IWebhookSignatureValidator validator, ICheckoutProvider provider, BillingService service, CancellationToken ct) =>
{
    System.Text.Json.JsonDocument body;
    try { body = await System.Text.Json.JsonDocument.ParseAsync(request.Body, cancellationToken: ct); }
    catch (System.Text.Json.JsonException) { return Results.UnprocessableEntity(new { codigo = "webhook_invalido" }); }
    using (body)
    {
        if (!body.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("id", out var idElement))
            return Results.UnprocessableEntity(new { codigo = "webhook_invalido" });
        if (!validator.IsValid(request.Headers["x-signature"].ToString(), request.Headers["x-request-id"].ToString(), idElement.ToString())) return Results.Unauthorized();
        var payment = await provider.ConsultarPagamentoAsync(idElement.ToString(), ct);
        if (payment is null) return Results.UnprocessableEntity(new { codigo = "pagamento_provedor_invalido" });
        try { await service.AplicarWebhookAsync(payment.Value.PagamentoId, payment.Value.Status, payment.Value.Referencia, ct); }
        catch (KeyNotFoundException) { return Results.UnprocessableEntity(new { codigo = "pagamento_desconhecido" }); }
        catch (PublicacaoResultadoException) { return Results.Json(new { codigo = "resultado_nao_publicado" }, statusCode: StatusCodes.Status503ServiceUnavailable); }
        catch (InvalidOperationException ex) { return Results.UnprocessableEntity(new { codigo = "transicao_invalida", detalhe = ex.Message }); }
        return Results.Accepted();
    }
}).AllowAnonymous();

app.MapPost("/api/v1/admin/dlq/reprocessar", async (HttpContext http, ReprocessadorDlq reprocessador, CancellationToken ct) =>
{
    if (!http.Request.HasJsonContentType()) return Results.UnprocessableEntity(new { codigo = "quantidade_invalida" });
    int quantidade;
    try
    {
        using var corpo = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
        if (corpo.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !corpo.RootElement.TryGetProperty("quantidade", out var q)
            || !q.TryGetInt32(out quantidade) || quantidade < 1 || quantidade > ReprocessadorDlq.QuantidadeMaxima)
            return Results.UnprocessableEntity(new { codigo = "quantidade_invalida" });
    }
    catch (System.Text.Json.JsonException) { return Results.UnprocessableEntity(new { codigo = "quantidade_invalida" }); }
    var autor = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.User.FindFirst("sub")?.Value ?? "desconhecido";
    return Results.Accepted(value: await reprocessador.ReprocessarAsync(quantidade, autor, ct));
}).RequireAuthorization("AdministrarDlq");
app.Run();
public partial class Program;
