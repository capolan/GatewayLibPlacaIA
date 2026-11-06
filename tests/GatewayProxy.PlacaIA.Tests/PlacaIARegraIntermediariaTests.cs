using GatewayProxy.PlacaIA;

namespace GatewayProxy.PlacaIA.Tests;

// Regra intermediaria (suprimirSeNaoConfirmouEVeiculoDivergir): a IA nao confirmou a placa E o veiculo
// (tipo/marca) E a cor divergem do cadastro. Caso real: OVI2A40 (2026-11-06, 8110).
public class PlacaIARegraIntermediariaTests
{
    private static PlacaIAJsonModel Cfg(bool regra = true, Action<PlacaIAJsonModel>? ajuste = null)
    {
        var c = new PlacaIAJsonModel { ativo = true, minConfianca = 0.5, suprimirSeNaoConfirmouEVeiculoDivergir = regra };
        ajuste?.Invoke(c);
        return c;
    }

    private static PlacaIALeituraModel Leitura(string placa, bool legivel = true, double conf = 0.95,
        string tipo = "carro", string marca = "Honda", string modelo = "City", string cor = "prata") => new()
    {
        placa = placa,
        legivel = legivel,
        confianca = conf,
        veiculo = new PlacaIAVeiculoModel { tipo = tipo, marca = marca, modelo = modelo, cor = cor }
    };

    // Cadastro do caso OVI2A40.
    private static VeiculoReferencia Cruze(string cor = "PRETA") => new()
    {
        descTipo = "AUTOMOVEL",
        descMarca = "CHEVROLET/CRUZE LTZ NB",
        descCor = cor,
        codTipo = 6
    };

    [Fact]
    public void CasoOVI2A40_IAIlegivel_HondaCityPrata_ContraCruzePreto_Suprime()
    {
        // A IA leu "OVI2Z" (incompleta, "?" implicito) com confianca 0,2: ilegivel.
        var leitura = Leitura("OVI2Z", legivel: false, conf: 0.2);
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", leitura, Cruze());

        Assert.Equal(PlacaIAComparador.Ilegivel, r.status);
        Assert.True(r.divergenciaVeiculo);
        Assert.True(r.divergenciaCor);
        Assert.True(r.suprimirAlerta);
        Assert.Contains("alerta suprimido", r.motivo);
        Assert.DoesNotContain("alerta mantido", r.motivo);
        Assert.Contains("cor IA: prata", r.detalheVeiculo);
    }

    [Fact]
    public void RegraDesligada_PorPadrao_MantemOAlerta()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(regra: false), "OVI2A40", Leitura("OVI2Z", legivel: false, conf: 0.2), Cruze());

        Assert.True(r.divergenciaVeiculo);
        Assert.True(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
        Assert.Contains("alerta mantido", r.motivo);
        Assert.False(new PlacaIAJsonModel().suprimirSeNaoConfirmouEVeiculoDivergir);
    }

    [Fact]
    public void BaixaConfianca_TambemConta_ComoNaoConfirmada()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2A40", conf: 0.3), Cruze());
        Assert.Equal(PlacaIAComparador.BaixaConfianca, r.status);
        Assert.True(r.suprimirAlerta);
    }

    [Fact]
    public void PlacaConfirmadaPelaIA_NuncaSuprime_PodeSerClone()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2A40", conf: 0.95), Cruze());
        Assert.Equal(PlacaIAComparador.Confirmada, r.status);
        Assert.True(r.divergenciaVeiculo);
        Assert.True(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
        Assert.Contains("possivel placa clonada", r.motivo);
    }

    [Fact]
    public void SoMarcaDiverge_CorIgual_MantemOAlerta()
    {
        // A IA erra marca com mais frequencia: sem a cor divergindo, nao derruba o alerta.
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2Z", legivel: false, conf: 0.2, cor: "preta"), Cruze());
        Assert.True(r.divergenciaVeiculo);
        Assert.False(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
        Assert.Contains("alerta mantido", r.motivo);
    }

    [Fact]
    public void SoCorDiverge_MarcaIgual_MantemOAlerta()
    {
        var cadastro = new VeiculoReferencia { descTipo = "AUTOMOVEL", descMarca = "HONDA/CITY EX", descCor = "PRETA" };
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2Z", legivel: false, conf: 0.2, cor: "prata"), cadastro);
        Assert.False(r.divergenciaVeiculo);
        Assert.True(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("outra")]
    public void CorDaIADesconhecida_NaoDispara(string? corIA)
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2Z", legivel: false, conf: 0.2, cor: corIA!), Cruze());
        Assert.False(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void CorDoCadastroDesconhecida_NaoDispara()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2Z", legivel: false, conf: 0.2), Cruze(cor: ""));
        Assert.False(r.divergenciaCor);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void SemReferencia_NaoSuprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "OVI2A40", Leitura("OVI2Z", legivel: false, conf: 0.2), null);
        Assert.False(r.suprimirAlerta);
        Assert.False(r.divergenciaCor);
    }

    [Fact]
    public void LeituraDeOutraPlaca_ContinuaSuprimindoPelaRegraAntiga()
    {
        // Nao depende da regra nova: divergente (legivel, outra placa, confianca >= min) ja suprime.
        var r = PlacaIAComparador.Avaliar(Cfg(regra: false), "OVI2A40", Leitura("ABC1D23", conf: 0.9), Cruze());
        Assert.Equal(PlacaIAComparador.Divergente, r.status);
        Assert.True(r.suprimirAlerta);
        Assert.DoesNotContain("alerta suprimido:", r.motivo);
    }

    [Fact]
    public void IAForaDoAr_NaoSuprimePelaRegra()
    {
        var r = PlacaIAComparador.NaoVerificadaResultado(Cfg(), "OVI2A40", "timeout");
        Assert.False(r.suprimirAlerta);
    }

    // ---- cores --------------------------------------------------------------------------------------------

    [Theory]
    // neutras: so diverge com distancia >= 2 na escala branca 0 / bege 0,5 / prata e dourada 1 / cinza 2 / preta 3
    [InlineData("prata", "PRETA", true)]
    [InlineData("branca", "CINZA", true)]
    [InlineData("branca", "PRETA", true)]
    [InlineData("bege", "PRETA", true)]
    [InlineData("branca", "PRATA", false)]
    [InlineData("prata", "BRANCA", false)]
    [InlineData("prata", "CINZA", false)]
    [InlineData("cinza", "PRETA", false)]
    [InlineData("preta", "PRETA", false)]
    [InlineData("branca", "BEGE", false)]
    // cromaticas: familias diferentes
    [InlineData("vermelha", "AZUL", true)]
    [InlineData("verde", "VERMELHA", true)]
    [InlineData("azul", "VERDE", true)]
    [InlineData("vermelha", "VINHO", false)]
    [InlineData("laranja", "VERMELHA", false)]
    [InlineData("amarela", "MARROM", false)]
    // cromatica x neutra: so diverge se a neutra for clara
    [InlineData("azul", "BRANCA", true)]
    [InlineData("vermelha", "PRATA", true)]
    [InlineData("branca", "VERDE", true)]
    [InlineData("azul", "PRETA", false)]
    [InlineData("preta", "AZUL", false)]
    [InlineData("verde", "CINZA", false)]
    // desconhecidas / fora da lista
    [InlineData("", "PRETA", false)]
    [InlineData("prata", null, false)]
    [InlineData("outra", "PRETA", false)]
    [InlineData("prata", "FANTASIA", false)]
    // masculino/feminino/acentos
    [InlineData("Prateado", "PRETO", true)]
    [InlineData("BRANCO", "Cinzento", true)]
    public void CorDiverge_Tabela(string? ia, string? cadastro, bool esperado)
    {
        Assert.Equal(esperado, PlacaIAComparador.CorDiverge(ia, cadastro));
    }

    [Fact]
    public void Prompt_PedeACor_ESchemaTemCor()
    {
        Assert.Contains("\"cor\"", PlacaIAPrompt.Texto);
        Assert.Contains("\"cor\"", PlacaIAPrompt.TextoPlacaPrincipal);
        var props = PlacaIAPrompt.Schema()["properties"]!["veiculo"]!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("cor"));
    }

    [Fact]
    public void Parse_LeACorQuandoVem_ENaoQuebraQuandoNaoVem()
    {
        var com = PlacaIAPrompt.Parse("{\"placa\":\"ABC1D23\",\"legivel\":true,\"confianca\":0.9," +
                                      "\"veiculo\":{\"tipo\":\"carro\",\"marca\":\"Honda\",\"modelo\":\"City\",\"cor\":\"prata\"}}");
        Assert.Equal("prata", com.veiculo!.cor);

        var sem = PlacaIAPrompt.Parse("{\"placa\":\"ABC1D23\",\"legivel\":true,\"confianca\":0.9," +
                                      "\"veiculo\":{\"tipo\":\"carro\",\"marca\":\"Honda\",\"modelo\":\"City\"}}");
        Assert.Null(sem.veiculo!.cor);
    }
}
