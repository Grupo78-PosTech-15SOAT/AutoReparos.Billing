using OpenTelemetry.Resources;

namespace AutoReparos.Billing.Api;

public static class TelemetriaBilling
{
    public static ResourceBuilder Configurar(ResourceBuilder recurso, string servico, string ambiente) =>
        recurso.AddService(servico)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", ambiente)]);
}
