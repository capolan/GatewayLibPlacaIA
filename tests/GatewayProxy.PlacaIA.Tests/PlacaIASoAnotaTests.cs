using GatewayProxy.PlacaIA;

namespace GatewayProxy.PlacaIA.Tests;

// Modo "so anota" (producao desde 2026-11-08: 8104, 8110 e 8105 com minConfianca 1.01): nenhuma leitura da IA suprime o
// alerta e a placa lida pela IA continua registrada. Motivo: em 44 divergencias conferidas a olho (OCR x PlacaIA, Haiku 5.5)
// o OCR estava certo em 21 e a IA em 14; a confianca da IA nao separa quem acertou (erradas 0,55-0,85, certas 0,65-0,85) e a
// regra antiga suprimiria 22% das leituras corretas do OCR.
public class PlacaIASoAnotaTests
{
    private static PlacaIAJsonModel Cfg() => new() { ativo = true, minConfianca = 1.01, suprimirSeVeiculoDivergir = false };

    private static PlacaIALeituraModel Leitura(string placa, double conf, bool legivel = true) => new()
    {
        placa = placa,
        legivel = legivel,
        confianca = conf,
        veiculo = new PlacaIAVeiculoModel { tipo = "carro", marca = "Honda", modelo = "City", cor = "prata" }
    };

    [Theory]
    [InlineData("TOH5G62", 0.70)]   // IA troca C por G (caso real da amostra: o OCR estava certo)
    [InlineData("DYF3494", 0.85)]   // IA le D onde era O
    [InlineData("ZZZ9Z99", 0.99)]   // placa totalmente diferente, confianca altissima
    [InlineData("ABC1D23", 1.00)]   // ate confianca 1,0 nao chega a 1,01
    public void LeituraDivergente_NaoSuprime_E_RegistraAPlacaDaIA(string placaIA, double conf)
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "TOH5C62", Leitura(placaIA, conf), null);

        Assert.False(r.suprimirAlerta);
        Assert.Equal(PlacaIAComparador.BaixaConfianca, r.status);
        Assert.Equal(placaIA, r.placaIA);                 // o que a IA leu fica registrado para revisao
        Assert.Contains("alerta mantido, revisar", r.motivo);
    }

    [Fact]
    public void LeituraIgual_NaoSuprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1D23", 0.95), null);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void IAIlegivel_NaoSuprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1?23", 0.4, legivel: false), null);
        Assert.False(r.suprimirAlerta);
    }
}
