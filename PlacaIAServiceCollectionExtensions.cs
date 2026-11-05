using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GatewayProxy.PlacaIA
{
    public static class PlacaIAServiceCollectionExtensions
    {
        // Registra o PlacaIAService (singleton), o HttpClient "placaia" e a configuracao vinda da secao
        // (ex.: builder.Configuration.GetSection("PlacaIA")). Com ativo=false nada chama a IA: quem usa
        // consulta PlacaIAService.Ativo antes de verificar/ler.
        // A chave de API vem da secao ou das variaveis PLACAIA_API_KEY / PLACAIA_FALLBACK_API_KEY.
        public static IServiceCollection AddPlacaIA(this IServiceCollection services, IConfiguration secao)
        {
            services.Configure<PlacaIAJsonModel>(secao);
            return services.AddPlacaIAServico();
        }

        // Sem secao: o chamador passa a configuracao a cada chamada (GatewayOne).
        public static IServiceCollection AddPlacaIAServico(this IServiceCollection services)
        {
            services.AddHttpClient("placaia");
            services.AddSingleton<PlacaIAService>();
            return services;
        }
    }
}
