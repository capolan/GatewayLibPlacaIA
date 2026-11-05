using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GatewayProxy.PlacaIA.Tests;

// Leitura sem placa esperada (PlacaPrincipal), registro por DI (ParkREST) e referencia de veiculo neutra.
public class PlacaIALeituraEIntegracaoTests
{
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };

    private static PlacaIALeituraModel Leitura(string placa, bool legivel = true, double conf = 0.95) => new()
    {
        placa = placa,
        legivel = legivel,
        confianca = conf,
        veiculo = new PlacaIAVeiculoModel { tipo = "carro", marca = "Fiat", modelo = "Argo" }
    };

    private sealed class Provedor : IPlacaVisionProvider
    {
        private readonly Func<PlacaIAModo, PlacaIALeituraModel> acao;
        public PlacaIAModo? ModoRecebido;
        public byte[]? RecorteRecebido;
        public Provedor(Func<PlacaIAModo, PlacaIALeituraModel> acao) => this.acao = acao;
        public string Nome => "falso";
        public string Modelo => "m";
        public Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            ModoRecebido = modo;
            RecorteRecebido = recorte;
            return Task.FromResult(acao(modo));
        }
    }

    private static PlacaIAService Servico(IPlacaVisionProvider p) =>
        new(NullLogger<PlacaIAService>.Instance, null!) { FabricaProvider = _ => p };

    #region AvaliarLeitura
    [Fact]
    public void AvaliarLeitura_PlacaCompletaConfiavel_ELida()
    {
        var r = PlacaIAComparador.AvaliarLeitura(new PlacaIAJsonModel { minConfianca = 0.5 }, Leitura("abc-1d23", conf: 0.6));
        Assert.Equal(PlacaIAComparador.Lida, r.status);
        Assert.Equal("ABC1D23", r.placaIA);
        Assert.False(r.suprimirAlerta);
    }

    [Theory]
    [InlineData("ABC1?23", true, 0.95, PlacaIAComparador.Ilegivel)]
    [InlineData("", true, 0.95, PlacaIAComparador.Ilegivel)]
    [InlineData("ABC1D23", false, 0.95, PlacaIAComparador.Ilegivel)]
    [InlineData("ABC1D23", true, 0.3, PlacaIAComparador.BaixaConfianca)]
    public void AvaliarLeitura_NaoConfiavel_NaoViraLida(string placa, bool legivel, double conf, string esperado)
    {
        var r = PlacaIAComparador.AvaliarLeitura(new PlacaIAJsonModel { minConfianca = 0.5 }, Leitura(placa, legivel, conf));
        Assert.Equal(esperado, r.status);
        Assert.False(r.suprimirAlerta);
    }
    #endregion

    #region Servico.LerAsync
    [Fact]
    public async Task LerAsync_PadraoUsaPlacaPrincipalESemRecorte()
    {
        var p = new Provedor(_ => Leitura("ABC1D23"));
        var r = await Servico(p).LerAsync(new PlacaIAJsonModel { minConfianca = 0.5 }, Jpeg);

        Assert.Equal(PlacaIAModo.PlacaPrincipal, p.ModoRecebido);
        Assert.Null(p.RecorteRecebido);
        Assert.Equal(PlacaIAComparador.Lida, r.status);
        Assert.Equal("ABC1D23", r.placaIA);
        Assert.False(r.recorteUsado);
    }

    [Fact]
    public async Task LerAsync_ProvedorFalha_VoltaNaoVerificadaSemLancar()
    {
        var svc = Servico(new FalhaSempre());
        var r = await svc.LerAsync(new PlacaIAJsonModel(), Jpeg);
        Assert.Equal(PlacaIAComparador.NaoVerificada, r.status);
        Assert.Contains("fora do ar", r.erro);
    }

    private sealed class FalhaSempre : IPlacaVisionProvider
    {
        public string Nome => "x";
        public string Modelo => "m";
        public Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde) => throw new HttpRequestException("fora do ar");
    }

    [Fact]
    public async Task Verificar_PlacaPrincipal_NaoGeraRecorteMesmoComRecortarLigado()
    {
        var p = new Provedor(_ => Leitura("ABC1D23"));
        var cfg = new PlacaIAJsonModel { recortarRetanguloVerde = true };
        var r = await Servico(p).VerificarAsync(cfg, Jpeg, "ABC1D23", null, modo: PlacaIAModo.PlacaPrincipal);

        Assert.Equal(PlacaIAModo.PlacaPrincipal, p.ModoRecebido);
        Assert.Null(p.RecorteRecebido);
        Assert.Equal(PlacaIAComparador.Confirmada, r.status);
    }

    [Fact]
    public async Task Verificar_SemModo_ContinuaNoRetanguloVerde()
    {
        var p = new Provedor(_ => Leitura("ABC1D23"));
        await Servico(p).VerificarAsync(new PlacaIAJsonModel(), Jpeg, "ABC1D23", null);
        Assert.Equal(PlacaIAModo.RetanguloVerde, p.ModoRecebido);
    }
    #endregion

    #region Prompt
    [Fact]
    public void Prompt_PlacaPrincipal_NaoMencionaRetanguloVerde()
    {
        var texto = PlacaIAPrompt.Montar(false, PlacaIAModo.PlacaPrincipal);
        Assert.DoesNotContain("GREEN", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legivel", texto);
        // Mesmo com "recorte" pedido, o modo sem retangulo nao fala em duas imagens.
        Assert.DoesNotContain("Two images", PlacaIAPrompt.Montar(true, PlacaIAModo.PlacaPrincipal));
    }

    [Fact]
    public void Prompt_PadraoContinuaOTextoDoRetanguloVerde()
    {
        Assert.Equal(PlacaIAPrompt.Texto, PlacaIAPrompt.Montar(false));
        Assert.StartsWith("Two images", PlacaIAPrompt.Montar(true));
    }
    #endregion

    #region Referencia de veiculo
    [Fact]
    public void Referencia_FonteApareceNoMotivoEDetalhe()
    {
        var cfg = new PlacaIAJsonModel { minConfianca = 0.5 };
        var r = PlacaIAComparador.Avaliar(cfg, "ABC1D23", Leitura("ABC1D23"),
            new VeiculoReferencia { descTipo = "MOTOCICLETA", descMarca = "HONDA/CG 160", codTipo = 4, fonte = "CADASTRO" });

        Assert.True(r.divergenciaVeiculo);
        Assert.Contains("CADASTRO", r.motivo);
        Assert.Contains("CADASTRO", r.detalheVeiculo);
    }

    [Fact]
    public void Referencia_PadraoDaFonteEDetranEs()
    {
        Assert.Equal("DETRAN-ES", new VeiculoReferencia().fonte);
    }
    #endregion

    #region DI (AddPlacaIA)
    private static ServiceProvider Monta(Dictionary<string, string?> valores)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddPlacaIA(cfg.GetSection("PlacaIA"));
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void AddPlacaIA_ResolveOServicoComAConfigDaSecao()
    {
        using var sp = Monta(new()
        {
            ["PlacaIA:ativo"] = "true",
            ["PlacaIA:provedor"] = "openrouter",
            ["PlacaIA:model"] = "anthropic/claude-haiku-5.5",
            ["PlacaIA:minConfianca"] = "0.5",
            ["PlacaIA:fallback:provedor"] = "ollama"
        });

        var svc = sp.GetRequiredService<PlacaIAService>();

        Assert.True(svc.Ativo);
        Assert.Equal("openrouter", svc.Config.provedor);
        Assert.Equal("anthropic/claude-haiku-5.5", svc.Config.model);
        Assert.Equal(0.5, svc.Config.minConfianca);
        Assert.Equal("ollama", svc.Config.fallback?.provedor);
    }

    [Fact]
    public void AddPlacaIA_SemSecaoNaoFicaAtivo()
    {
        using var sp = Monta(new());
        Assert.False(sp.GetRequiredService<PlacaIAService>().Ativo);
    }

    [Fact]
    public void AddPlacaIAServico_SemConfig_FalhaClaraAoPedirAConfig()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddPlacaIAServico();
        using var sp = sc.BuildServiceProvider();
        var svc = sp.GetRequiredService<PlacaIAService>();

        Assert.False(svc.Ativo);
        // IOptionsMonitor existe (AddHttpClient registra Options), mas sem a secao a config e o padrao: ativo=false.
        Assert.False(svc.Config.ativo);
    }
    #endregion
}
