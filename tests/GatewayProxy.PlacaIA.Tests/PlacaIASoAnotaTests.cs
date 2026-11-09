using GatewayProxy.PlacaIA;

namespace GatewayProxy.PlacaIA.Tests;

// Modo "so anota" da placa: em producao desde 2026-11-08 (8104, 8110 e 8105). Em 44 divergencias OCR x IA conferidas a olho
// (Haiku 5.5) o OCR estava certo em 21 e a IA em 14; a confianca da IA nao separa certas de erradas (erradas 0,55-0,85, certas
// 0,65-0,85) e a regra antiga suprimiria 22% das leituras corretas do OCR.
//   Fase 1 (2026-11-08): minConfianca 1.01 (nenhuma leitura chega la). Efeito colateral: a IA nunca "confirma" a placa.
//   Fase 2 (2026-11-09): suprimirSeDivergente=false + minConfianca 0.5 + regra intermediaria ligada. A leitura de outra placa so
//   anota, a placa igual continua "confirmada", e so suprime quando a placa nao foi confirmada E veiculo e cor divergem.
public class PlacaIASoAnotaTests
{
    private static PlacaIAJsonModel Cfg1() => new() { ativo = true, minConfianca = 1.01, suprimirSeVeiculoDivergir = false };

    private static PlacaIAJsonModel Cfg2(bool regra = true) => new()
    {
        ativo = true, minConfianca = 0.5, suprimirSeDivergente = false, suprimirSeVeiculoDivergir = false,
        suprimirSeNaoConfirmouEVeiculoDivergir = regra
    };

    private static PlacaIALeituraModel Leitura(string placa, double conf, bool legivel = true, string tipo = "carro",
        string marca = "Volkswagen", string modelo = "Jetta", string cor = "prata") => new()
    {
        placa = placa, legivel = legivel, confianca = conf,
        veiculo = new PlacaIAVeiculoModel { tipo = tipo, marca = marca, modelo = modelo, cor = cor }
    };

    private static VeiculoReferencia I30Preto() => new() { descTipo = "AUTOMOVEL", descMarca = "I/HYUNDAI I30 2.0", descCor = "PRETA", codTipo = 6, fonte = "SESP-PR" };

    // ---------- fase 1: minConfianca 1.01 ----------

    [Theory]
    [InlineData("TOH5G62", 0.70)]
    [InlineData("DYF3494", 0.85)]
    [InlineData("ZZZ9Z99", 0.99)]
    [InlineData("ABC1D23", 1.00)]
    public void MinConfianca101_LeituraDivergente_NaoSuprime_E_RegistraAPlacaDaIA(string placaIA, double conf)
    {
        var r = PlacaIAComparador.Avaliar(Cfg1(), "TOH5C62", Leitura(placaIA, conf), null);

        Assert.False(r.suprimirAlerta);
        Assert.Equal(PlacaIAComparador.BaixaConfianca, r.status);
        Assert.Equal(placaIA, r.placaIA);
        Assert.Contains("alerta mantido, revisar", r.motivo);
    }

    // ---------- fase 2: suprimirSeDivergente=false ----------

    [Theory]
    [InlineData("TOH5G62", 0.70)]
    [InlineData("DYF3494", 0.85)]
    [InlineData("ZZZ9Z99", 0.99)]
    public void LeituraDeOutraPlaca_SoAnota_MantemOAlerta(string placaIA, double conf)
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(regra: false), "TOH5C62", Leitura(placaIA, conf), null);

        Assert.False(r.suprimirAlerta);
        Assert.Equal(PlacaIAComparador.Divergente, r.status);       // o status continua dizendo a verdade
        Assert.Equal(placaIA, r.placaIA);
        Assert.Contains("alerta mantido, revisar", r.motivo);
    }

    [Fact]
    public void PlacaIgual_ContinuaConfirmada()
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(), "ABC1D23", Leitura("ABC1D23", 0.9), I30Preto());
        Assert.Equal(PlacaIAComparador.Confirmada, r.status);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void PlacaConfirmada_ComVeiculoECorDivergentes_NaoSuprime_PossivelClone()
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(), "AFR9810", Leitura("AFR9810", 0.9), I30Preto());
        Assert.True(r.divergenciaVeiculo);
        Assert.True(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
        Assert.Contains("possivel placa clonada", r.motivo);
    }

    // Os dois casos do cismelone (2026-11-08 e 09): IA nao leu a placa e viu outro carro.
    [Theory]
    [InlineData("AFR9B1", false, 0.4)]   // ilegivel (leitura truncada)
    [InlineData("AFR9B10", true, 0.4)]   // baixa confianca
    public void PlacaNaoConfirmada_ComVeiculoECorDivergentes_Suprime(string placaIA, bool legivel, double conf)
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(), "AFR9810", Leitura(placaIA, conf, legivel), I30Preto());
        Assert.True(r.suprimirAlerta);
        Assert.Contains("alerta suprimido", r.motivo);
    }

    [Fact]
    public void OutraPlacaConfiante_ComVeiculoECorDivergentes_Suprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(), "AFR9810", Leitura("AFR9B10", 0.8), I30Preto());
        Assert.Equal(PlacaIAComparador.Divergente, r.status);
        Assert.True(r.suprimirAlerta);
        Assert.Contains("alerta suprimido", r.motivo);
    }

    [Fact]
    public void OutraPlaca_ComSoACorDivergente_NaoSuprime()
    {
        // Mesmo tipo e marca do cadastro, so a cor difere: divergencia isolada nao basta.
        var r = PlacaIAComparador.Avaliar(Cfg2(), "AFR9810", Leitura("AFR9B10", 0.8, marca: "Hyundai", modelo: "i30", cor: "prata"), I30Preto());
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void OutraPlaca_ComVeiculoECorIguaisAoCadastro_NaoSuprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(), "AFR9810", Leitura("AFR9B10", 0.8, marca: "Hyundai", modelo: "i30", cor: "preta"), I30Preto());
        Assert.False(r.suprimirAlerta);
    }

    // ---------- padrao inalterado ----------

    [Fact]
    public void Padrao_SuprimirSeDivergente_ContinuaTrue_EOutraPlacaSuprime()
    {
        var cfg = new PlacaIAJsonModel { ativo = true, minConfianca = 0.5 };
        Assert.True(cfg.suprimirSeDivergente);
        var r = PlacaIAComparador.Avaliar(cfg, "ABC1D23", Leitura("ZZZ9Z99", 0.9), null);
        Assert.True(r.suprimirAlerta);
    }

    [Fact]
    public void IAIlegivel_SemVeiculoDivergente_NaoSuprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg2(), "ABC1D23", Leitura("ABC1?23", 0.4, legivel: false, marca: "Hyundai", modelo: "i30", cor: "preta"), I30Preto());
        Assert.False(r.suprimirAlerta);
    }
}
