using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GatewayProxy.PlacaIA
{
    // Verifica/le placas com uma IA de visao. Singleton: so guarda dependencias; a configuracao vem do
    // chamador (GatewayOne: oneConfig.json) ou de IOptionsMonitor<PlacaIAJsonModel> (AddPlacaIA, ParkREST).
    public class PlacaIAService
    {
        private readonly ILogger<PlacaIAService> logger;
        private readonly IHttpClientFactory httpClientFactory;
        private readonly IOptionsMonitor<PlacaIAJsonModel>? opcoes;

        // Permite trocar a criacao do provedor (testes).
        public Func<PlacaIAProvedorModel, IPlacaVisionProvider>? FabricaProvider { get; set; }

        public PlacaIAService(ILogger<PlacaIAService> logger, IHttpClientFactory httpClientFactory,
            IOptionsMonitor<PlacaIAJsonModel>? opcoes = null)
        {
            this.logger = logger;
            this.httpClientFactory = httpClientFactory;
            this.opcoes = opcoes;
        }

        // Config da secao registrada com AddPlacaIA. Falha clara se o consumidor nao registrou.
        public PlacaIAJsonModel Config =>
            opcoes?.CurrentValue ?? throw new InvalidOperationException(
                "PlacaIA: sem configuracao registrada; use AddPlacaIA(secao) ou passe a configuracao ao metodo");

        public bool Ativo => opcoes?.CurrentValue.ativo ?? false;

        // Confere a placa que o LPR leu (placaLida) com a leitura da IA.
        public Task<PlacaIAResultModel> VerificarAsync(byte[] jpeg, string placaLida, VeiculoReferencia? referencia = null,
            string? processId = null, PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
            => VerificarAsync(Config, jpeg, placaLida, referencia, processId, modo);

        public async Task<PlacaIAResultModel> VerificarAsync(PlacaIAJsonModel cfg, byte[] jpeg, string placaLida,
            VeiculoReferencia? referencia, string? processId = null, PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            var sw = Stopwatch.StartNew();
            var tentativas = Tentativas(cfg);
            var recorte = GeraRecorte(cfg, jpeg, modo, processId);

            string? ultimoErro = null;
            for (int i = 0; i < tentativas.Count; i++)
            {
                var prov = tentativas[i];
                try
                {
                    var provider = CriaProvider(prov, fallback: i > 0);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, prov.timeoutSeg)));
                    var leitura = await provider.LerAsync(jpeg, recorte, cts.Token, modo).ConfigureAwait(false);
                    var r = PlacaIAComparador.Avaliar(cfg, placaLida, leitura, referencia);
                    r.recorteUsado = recorte != null;
                    r.provedor = provider.Nome;
                    r.modelo = provider.Modelo;
                    r.usouFallback = i > 0;
                    r.ms = sw.ElapsedMilliseconds;
                    logger.LogInformation("[{ProcessId}] PlacaIA {Provedor}/{Modelo}: lida={Lida} ia={IA} conf={Conf:P0} status={Status} " +
                        "veiculoIA=[{Tipo}|{Marca}|{ModeloVeic}|{CorIA}] detran=[{DetranTipo}|{DetranMarca}|{DetranCor}] veiculoDiverge={Vd} corDiverge={Cd} suprimir={Sup} recorte={Recorte} tokens={TokIn}/{TokOut} ({Ms}ms)",
                        processId, r.provedor, r.modelo, placaLida, r.placaIA, r.confianca, r.status,
                        r.veiculo?.tipo, r.veiculo?.marca, r.veiculo?.modelo, r.veiculo?.cor,
                        referencia?.descTipo, referencia?.descMarca, referencia?.descCor,
                        r.divergenciaVeiculo, r.divergenciaCor, r.suprimirAlerta, r.recorteUsado, r.tokensEntrada, r.tokensSaida, r.ms);
                    return r;
                }
                catch (Exception ex)
                {
                    ultimoErro = RegistraFalha(ex, prov, i, tentativas.Count, processId);
                }
            }

            var falha = PlacaIAComparador.NaoVerificadaResultado(cfg, placaLida, ultimoErro ?? "falha desconhecida");
            falha.provedor = cfg.provedor;
            falha.modelo = cfg.model;
            falha.ms = sw.ElapsedMilliseconds;
            return falha;
        }

        // Le a placa da imagem SEM uma placa esperada (leitor de reserva, ex.: ParkREST). Status: lida,
        // ilegivel, baixaConfianca ou naoVerificada (IA fora do ar / timeout; falha do provedor nunca lanca).
        public Task<PlacaIAResultModel> LerAsync(byte[] jpeg, string? processId = null,
            PlacaIAModo modo = PlacaIAModo.PlacaPrincipal)
            => LerAsync(Config, jpeg, processId, modo);

        public async Task<PlacaIAResultModel> LerAsync(PlacaIAJsonModel cfg, byte[] jpeg, string? processId = null,
            PlacaIAModo modo = PlacaIAModo.PlacaPrincipal)
        {
            var sw = Stopwatch.StartNew();
            var tentativas = Tentativas(cfg);
            var recorte = GeraRecorte(cfg, jpeg, modo, processId);

            string? ultimoErro = null;
            for (int i = 0; i < tentativas.Count; i++)
            {
                var prov = tentativas[i];
                try
                {
                    var provider = CriaProvider(prov, fallback: i > 0);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, prov.timeoutSeg)));
                    var leitura = await provider.LerAsync(jpeg, recorte, cts.Token, modo).ConfigureAwait(false);
                    var r = PlacaIAComparador.AvaliarLeitura(cfg, leitura);
                    r.recorteUsado = recorte != null;
                    r.provedor = provider.Nome;
                    r.modelo = provider.Modelo;
                    r.usouFallback = i > 0;
                    r.ms = sw.ElapsedMilliseconds;
                    logger.LogInformation("[{ProcessId}] PlacaIA leitura {Provedor}/{Modelo}: ia={IA} conf={Conf:P0} status={Status} " +
                        "veiculoIA=[{Tipo}|{Marca}|{ModeloVeic}] tokens={TokIn}/{TokOut} ({Ms}ms)",
                        processId, r.provedor, r.modelo, r.placaIA, r.confianca, r.status,
                        r.veiculo?.tipo, r.veiculo?.marca, r.veiculo?.modelo, r.tokensEntrada, r.tokensSaida, r.ms);
                    return r;
                }
                catch (Exception ex)
                {
                    ultimoErro = RegistraFalha(ex, prov, i, tentativas.Count, processId);
                }
            }

            return new PlacaIAResultModel
            {
                status = PlacaIAComparador.NaoVerificada,
                erro = ultimoErro ?? "falha desconhecida",
                motivo = "IA indisponivel (placa nao lida)",
                provedor = cfg.provedor,
                modelo = cfg.model,
                ms = sw.ElapsedMilliseconds
            };
        }

        private static List<PlacaIAProvedorModel> Tentativas(PlacaIAJsonModel cfg)
        {
            var tentativas = new List<PlacaIAProvedorModel> { cfg };
            if (cfg.fallback != null) { tentativas.Add(cfg.fallback); }
            return tentativas;
        }

        // Recorte ampliado do retangulo verde (2a imagem). Falha aqui nunca derruba a chamada: sem recorte,
        // segue so com a imagem inteira. Sem retangulo (PlacaPrincipal) nao ha o que recortar.
        private byte[]? GeraRecorte(PlacaIAJsonModel cfg, byte[] jpeg, PlacaIAModo modo, string? processId)
        {
            if (!cfg.recortarRetanguloVerde || modo != PlacaIAModo.RetanguloVerde) { return null; }
            try
            {
                var recorte = PlacaIARecorte.Gerar(jpeg, out var infoRecorte);
                logger.LogDebug("[{ProcessId}] PlacaIA recorte: {Info}", processId, infoRecorte);
                return recorte;
            }
            catch (Exception ex)
            {
                logger.LogWarning("[{ProcessId}] PlacaIA: falha ao gerar o recorte ({Erro}); segue com a imagem inteira",
                    processId, ex.Message);
                return null;
            }
        }

        private string RegistraFalha(Exception ex, PlacaIAProvedorModel prov, int indice, int total, string? processId)
        {
            var erro = ex is OperationCanceledException
                ? $"{prov.provedor}: timeout ({prov.timeoutSeg}s)"
                : ex.Message;
            if (erro.Length > 300) { erro = erro.Substring(0, 300); }
            logger.LogWarning("[{ProcessId}] PlacaIA {Provedor} falhou ({Tentativa}/{Total}): {Erro}",
                processId, prov.provedor, indice + 1, total, erro);
            return erro;
        }

        // Variaveis de ambiente que substituem apiKey quando ela esta vazia na configuracao
        // (o arquivo e versionado: a chave nao deve ficar nele).
        public const string EnvApiKey = "PLACAIA_API_KEY";
        public const string EnvApiKeyFallback = "PLACAIA_FALLBACK_API_KEY";

        // Provedores que falam o protocolo OpenAI, mas tem endereco proprio. Sem isto, baseUrl vazio
        // cairia no endereco da OpenAI e a chave do outro provedor seria enviada para la.
        private static readonly Dictionary<string, string> BaseUrlPorProvedor = new()
        {
            ["openrouter"] = "https://openrouter.ai/api/v1",
            ["deepseek"] = "https://api.deepseek.com/v1",
            ["xai"] = "https://api.x.ai/v1"
        };

        // Copia da configuracao com baseUrl padrao do provedor e chave vinda do ambiente quando faltarem.
        public static PlacaIAProvedorModel Resolve(PlacaIAProvedorModel prov, bool fallback)
        {
            var nome = (prov.provedor ?? "").Trim().ToLowerInvariant();
            var r = new PlacaIAProvedorModel
            {
                provedor = prov.provedor,
                model = prov.model,
                apiKey = prov.apiKey,
                baseUrl = prov.baseUrl,
                timeoutSeg = prov.timeoutSeg,
                esforco = prov.esforco,
                temperatura = prov.temperatura,
                keepAlive = prov.keepAlive
            };
            if (string.IsNullOrWhiteSpace(r.baseUrl) && BaseUrlPorProvedor.TryGetValue(nome, out var url))
            {
                r.baseUrl = url;
            }
            if (string.IsNullOrWhiteSpace(r.apiKey))
            {
                r.apiKey = Environment.GetEnvironmentVariable(fallback ? EnvApiKeyFallback : EnvApiKey);
            }
            return r;
        }

        private IPlacaVisionProvider CriaProvider(PlacaIAProvedorModel original, bool fallback)
        {
            var prov = Resolve(original, fallback);
            if (FabricaProvider != null) { return FabricaProvider(prov); }
            var http = httpClientFactory.CreateClient("placaia");
            // O prazo real e o CancellationToken (timeoutSeg); o timeout padrao do cliente (100s) nao deve cortar antes.
            http.Timeout = Timeout.InfiniteTimeSpan;
            return (prov.provedor ?? "").Trim().ToLowerInvariant() switch
            {
                "anthropic" or "claude" => new AnthropicPlacaProvider(http, prov),
                "gemini" or "google" => new GeminiPlacaProvider(http, prov),
                "openai" or "chatgpt" or "deepseek" or "openrouter" or "xai" => new OpenAiCompatPlacaProvider(http, prov),
                "ollama" => new OllamaPlacaProvider(http, prov),
                _ => throw new InvalidOperationException($"PlacaIA: provedor desconhecido '{prov.provedor}'")
            };
        }
    }
}
