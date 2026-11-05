using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GatewayProxy.PlacaIA;
using Microsoft.Extensions.Logging.Abstractions;

namespace GatewayProxy.PlacaIA.Tests;

public class PlacaIATests
{
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };

    private static PlacaIAJsonModel Cfg(Action<PlacaIAJsonModel>? ajuste = null)
    {
        var c = new PlacaIAJsonModel { ativo = true, minConfianca = 0.8 };
        ajuste?.Invoke(c);
        return c;
    }

    private static PlacaIALeituraModel Leitura(string placa, bool legivel = true, double conf = 0.95,
        string tipo = "carro", string marca = "Volkswagen", string modelo = "Gol") => new()
    {
        placa = placa,
        legivel = legivel,
        confianca = conf,
        veiculo = new PlacaIAVeiculoModel { tipo = tipo, marca = marca, modelo = modelo }
    };

    private static VeiculoReferencia Detran(string tipo = "AUTOMOVEL", string marca = "VW/GOL 1.0", int? cod = 1) => new()
    {
        descTipo = tipo,
        descMarca = marca,
        codTipo = cod
    };

    #region Comparador
    [Fact]
    public void Placa_Igual_ConfirmaENaoSuprime()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("abc-1d23"), Detran());
        Assert.Equal(PlacaIAComparador.Confirmada, r.status);
        Assert.False(r.suprimirAlerta);
        Assert.Equal("ABC1D23", r.placaIA);
    }

    [Fact]
    public void Placa_Diferente_SuprimeERegistraALeituraDaIA()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1D28"), Detran());
        Assert.Equal(PlacaIAComparador.Divergente, r.status);
        Assert.True(r.suprimirAlerta);
        Assert.Contains("ABC1D28", r.motivo);
    }

    [Fact]
    public void Confusao_ZeroLetraO_NaoEPerdoada()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC0123", Leitura("ABCO123"), null);
        Assert.Equal(PlacaIAComparador.Divergente, r.status);
        Assert.True(r.suprimirAlerta);
    }

    [Theory]
    [InlineData("ABC1D23", false, 0.99)]
    [InlineData("ABC1?23", true, 0.99)]
    [InlineData("", true, 0.99)]
    public void Ilegivel_MantemOAlertaParaRevisao_PorPadrao(string placa, bool legivel, double conf)
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura(placa, legivel, conf), null);
        Assert.Equal(PlacaIAComparador.Ilegivel, r.status);
        Assert.False(r.suprimirAlerta);
        Assert.Contains("revisar", r.motivo);
    }

    [Fact]
    public void Ilegivel_Suprime_QuandoSuprimirSeIlegivel()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(c => c.suprimirSeIlegivel = true), "ABC1D23", Leitura("ABC1?23", true, 0.99), null);
        Assert.True(r.suprimirAlerta);
        Assert.DoesNotContain("revisar", r.motivo);
    }

    [Fact]
    public void BaixaConfianca_MantemOAlerta_MesmoComPlacaIgual()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1D23", conf: 0.5), null);
        Assert.Equal(PlacaIAComparador.BaixaConfianca, r.status);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void OutraPlacaComConfiancaBaixa_NaoSuprime()
    {
        // Leitura divergente mas hesitante nao e evidencia suficiente contra o alerta.
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC7D23", conf: 0.55), null);
        Assert.Equal(PlacaIAComparador.BaixaConfianca, r.status);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void BaixaConfianca_Suprime_QuandoSuprimirSeIlegivel()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(c => c.suprimirSeIlegivel = true), "ABC1D23", Leitura("ABC1D23", conf: 0.5), null);
        Assert.True(r.suprimirAlerta);
    }

    [Fact]
    public void CasoFindFaces18885325_SonnetLeuAPlacaCertaComConfiancaBaixa_AlertaMantidoEVeiculoDivergeInformado()
    {
        // Placa RBA2H01 (DETRAN: Fiat Argo prata) numa SUV escura: a IA leu RBA2H01 com 0,6 e viu outro veiculo.
        var r = PlacaIAComparador.Avaliar(Cfg(), "RBA2H01",
            Leitura("RBA2H01", conf: 0.6, tipo: "suv", marca: "Toyota", modelo: "RAV4"),
            Detran("AUTOMOVEL", "FIAT/ARGO DRIVE 1.0"));
        Assert.Equal(PlacaIAComparador.BaixaConfianca, r.status);
        Assert.False(r.suprimirAlerta);
        Assert.True(r.divergenciaVeiculo);
        Assert.Contains("revisar", r.motivo);
    }

    [Fact]
    public void Veiculo_Compativel_NaoDiverge()
    {
        // "VW/GOL 1.0" no DETRAN x Volkswagen Gol na IA (VW e alias de Volkswagen).
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1D23"), Detran());
        Assert.False(r.divergenciaVeiculo);
        Assert.False(r.suprimirAlerta);
    }

    [Fact]
    public void Veiculo_TipoDiferente_DivergeMasSoInformaPorPadrao()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1D23", tipo: "moto", marca: "Honda", modelo: "CG 160"),
            Detran("AUTOMOVEL", "VW/GOL"));
        Assert.True(r.divergenciaVeiculo);
        Assert.False(r.suprimirAlerta);
        Assert.Contains("placa clonada", r.motivo);
    }

    [Fact]
    public void Veiculo_MarcaDiferente_SuprimeSeConfigurado()
    {
        var cfg = Cfg(c => c.suprimirSeVeiculoDivergir = true);
        var r = PlacaIAComparador.Avaliar(cfg, "ABC1D23", Leitura("ABC1D23", marca: "Ford", modelo: "Ka"),
            Detran("AUTOMOVEL", "FIAT/UNO"));
        Assert.True(r.divergenciaVeiculo);
        Assert.True(r.suprimirAlerta);
    }

    [Fact]
    public void Veiculo_CamionetaESuvPicape_SaoMesmaCategoria()
    {
        var r = PlacaIAComparador.Avaliar(Cfg(), "ABC1D23", Leitura("ABC1D23", tipo: "picape", marca: "Toyota", modelo: "Hilux"),
            Detran("CAMIONETA", "TOYOTA/HILUX CD"));
        Assert.False(r.divergenciaVeiculo);
    }

    [Fact]
    public void Veiculo_DetranMotoPorCodigo_ContraCarroDaIA_Diverge()
    {
        Assert.True(PlacaIAComparador.VeiculoDiverge(new PlacaIAVeiculoModel { tipo = "carro", marca = "Honda" },
            Detran("", "HONDA/CG 160", 4), out _));
    }

    [Fact]
    public void Veiculo_SemDadoNoDetran_NaoDiverge()
    {
        Assert.False(PlacaIAComparador.VeiculoDiverge(new PlacaIAVeiculoModel { tipo = "carro", marca = "Fiat" },
            Detran("", ""), out _));
    }
    #endregion

    #region Parse
    [Fact]
    public void Parse_AceitaCercaMarkdownETextoEmVolta()
    {
        var l = PlacaIAPrompt.Parse("Aqui:\n```json\n{\"placa\":\"ABC1D23\",\"legivel\":true,\"confianca\":0.9," +
            "\"veiculo\":{\"tipo\":\"suv\",\"marca\":\"Jeep\",\"modelo\":\"Compass\"}}\n```");
        Assert.Equal("ABC1D23", l.placa);
        Assert.True(l.legivel);
        Assert.Equal(0.9, l.confianca, 3);
        Assert.Equal("suv", l.veiculo!.tipo);
    }

    [Fact]
    public void Parse_ConfiancaEmPercentual_Normaliza()
    {
        var l = PlacaIAPrompt.Parse("{\"placa\":\"ABC1D23\",\"legivel\":\"true\",\"confianca\":\"95\"}");
        Assert.Equal(0.95, l.confianca, 3);
        Assert.True(l.legivel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem json")]
    [InlineData("{quebrado")]
    public void Parse_Invalido_Lanca(string txt)
    {
        Assert.Throws<InvalidOperationException>(() => PlacaIAPrompt.Parse(txt));
    }
    #endregion

    #region Provedores
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Req;
        public JsonNode? Body;
        public Func<HttpResponseMessage> Resposta = () => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Req = request;
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            return Resposta();
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode s = HttpStatusCode.OK) =>
        new(s) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string Saida = "{\"placa\":\"ABC1D23\",\"legivel\":true,\"confianca\":0.97," +
        "\"veiculo\":{\"tipo\":\"carro\",\"marca\":\"Fiat\",\"modelo\":\"Uno\"}}";

    // Texto como literal JSON (aspas e escapes) para embutir no corpo de resposta falso.
    private static string Escapa(string s) => JsonValue.Create(s)!.ToJsonString();

    [Fact]
    public async Task Anthropic_MontaRequestSemTemperatureEIgnoraBlocoThinking()
    {
        var h = new FakeHandler
        {
            Resposta = () => Json("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"\"}," +
                "{\"type\":\"text\",\"text\":" + Escapa(Saida) + "}]}")
        };
        var p = new AnthropicPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "k", model = "claude-sonnet-5-5" });

        var l = await p.LerAsync(Jpeg, null, CancellationToken.None);

        Assert.Equal("ABC1D23", l.placa);
        Assert.Equal("https://api.anthropic.com/v1/messages", h.Req!.RequestUri!.ToString());
        Assert.Equal("k", h.Req.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", h.Req.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("claude-sonnet-5-5", (string?)h.Body!["model"]);
        Assert.Null(h.Body["temperature"]);
        Assert.Equal("low", (string?)h.Body["output_config"]!["effort"]);
        var img = h.Body["messages"]![0]!["content"]![0]!;
        Assert.Equal("image", (string?)img["type"]);
        Assert.Equal("image/jpeg", (string?)img["source"]!["media_type"]);
        Assert.Equal(Convert.ToBase64String(Jpeg), (string?)img["source"]!["data"]);
    }

    [Fact]
    public async Task Anthropic_ComRecorte_EnviaDuasImagensEPromptComRecorte()
    {
        var h = new FakeHandler
        {
            Resposta = () => Json("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":" + Escapa(Saida) + "}]}")
        };
        var p = new AnthropicPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "k" });
        var recorte = new byte[] { 1, 2, 3 };

        await p.LerAsync(Jpeg, recorte, CancellationToken.None);

        var conteudo = (JsonArray)h.Body!["messages"]![0]!["content"]!;
        Assert.Equal(3, conteudo.Count); // imagem inteira, recorte, texto
        Assert.Equal(Convert.ToBase64String(Jpeg), (string?)conteudo[0]!["source"]!["data"]);
        Assert.Equal(Convert.ToBase64String(recorte), (string?)conteudo[1]!["source"]!["data"]);
        Assert.Contains("Two images are attached", (string?)conteudo[2]!["text"]);
    }

    [Fact]
    public async Task OpenAiCompat_ComRecorte_EnviaDuasImagens_SemRecorteEnviaUma()
    {
        var h = new FakeHandler { Resposta = () => Json("{\"choices\":[{\"message\":{\"content\":" + Escapa(Saida) + "}}]}") };
        var p = new OpenAiCompatPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "d" });

        await p.LerAsync(Jpeg, new byte[] { 9 }, CancellationToken.None);
        Assert.Equal(3, ((JsonArray)h.Body!["messages"]![0]!["content"]!).Count); // texto + 2 imagens

        await p.LerAsync(Jpeg, null, CancellationToken.None);
        var c = (JsonArray)h.Body!["messages"]![0]!["content"]!;
        Assert.Equal(2, c.Count);
        Assert.DoesNotContain("Two images", (string?)c[0]!["text"]);
    }

    [Fact]
    public async Task Gemini_E_Ollama_ComRecorte_EnviamDuasImagens()
    {
        var hg = new FakeHandler { Resposta = () => Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + Escapa(Saida) + "}]}}]}") };
        await new GeminiPlacaProvider(new HttpClient(hg), new PlacaIAProvedorModel { apiKey = "g" })
            .LerAsync(Jpeg, new byte[] { 9 }, CancellationToken.None);
        Assert.Equal(3, ((JsonArray)hg.Body!["contents"]![0]!["parts"]!).Count);

        var ho = new FakeHandler { Resposta = () => Json("{\"message\":{\"content\":" + Escapa(Saida) + "}}") };
        await new OllamaPlacaProvider(new HttpClient(ho), new PlacaIAProvedorModel())
            .LerAsync(Jpeg, new byte[] { 9 }, CancellationToken.None);
        Assert.Equal(2, ((JsonArray)ho.Body!["messages"]![0]!["images"]!).Count);
    }

    [Fact]
    public async Task Anthropic_Refusal_Lanca()
    {
        var h = new FakeHandler { Resposta = () => Json("{\"stop_reason\":\"refusal\",\"content\":[]}") };
        var p = new AnthropicPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "k" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.LerAsync(Jpeg, null, CancellationToken.None));
    }

    [Fact]
    public async Task Anthropic_Http401_LancaSemVazarAChave()
    {
        var h = new FakeHandler { Resposta = () => Json("{\"error\":\"invalid x-api-key\"}", HttpStatusCode.Unauthorized) };
        var p = new AnthropicPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "segredo-123" });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.LerAsync(Jpeg, null, CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("segredo-123", ex.Message);
    }

    [Fact]
    public async Task Gemini_MontaRequestEJuntaPartes()
    {
        var h = new FakeHandler
        {
            Resposta = () => Json("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + Escapa(Saida) + "}]}}]}")
        };
        var p = new GeminiPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "g", model = "gemini-2.5-flash" });

        var l = await p.LerAsync(Jpeg, null, CancellationToken.None);

        Assert.Equal("ABC1D23", l.placa);
        Assert.EndsWith("/v1beta/models/gemini-2.5-flash:generateContent", h.Req!.RequestUri!.AbsolutePath);
        Assert.Equal("g", h.Req.Headers.GetValues("x-goog-api-key").Single());
        Assert.Equal("image/jpeg", (string?)h.Body!["contents"]![0]!["parts"]![0]!["inline_data"]!["mime_type"]);
        Assert.Equal("application/json", (string?)h.Body["generationConfig"]!["responseMimeType"]);
    }

    [Fact]
    public async Task Gemini_BloqueadoSemCandidates_Lanca()
    {
        var h = new FakeHandler { Resposta = () => Json("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}") };
        var p = new GeminiPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { apiKey = "g" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.LerAsync(Jpeg, null, CancellationToken.None));
    }

    [Fact]
    public async Task OpenAiCompat_UsaBaseUrlConfigurada_ImagemDataUri()
    {
        var h = new FakeHandler
        {
            Resposta = () => Json("{\"choices\":[{\"message\":{\"content\":" + Escapa(Saida) + "}}]," +
                "\"usage\":{\"prompt_tokens\":1234,\"completion_tokens\":56}}")
        };
        var p = new OpenAiCompatPlacaProvider(new HttpClient(h),
            new PlacaIAProvedorModel { apiKey = "d", model = "modelo-x", baseUrl = "https://api.exemplo.com/v1/" });

        var l = await p.LerAsync(Jpeg, null, CancellationToken.None);

        Assert.Equal("ABC1D23", l.placa);
        Assert.Equal(1234, l.tokensEntrada);
        Assert.Equal(56, l.tokensSaida);
        Assert.Equal("https://api.exemplo.com/v1/chat/completions", h.Req!.RequestUri!.ToString());
        Assert.Equal("Bearer", h.Req.Headers.Authorization!.Scheme);
        Assert.Equal("d", h.Req.Headers.Authorization.Parameter);
        var url = (string?)h.Body!["messages"]![0]!["content"]![1]!["image_url"]!["url"];
        Assert.StartsWith("data:image/jpeg;base64,", url);
    }

    [Fact]
    public async Task Ollama_UsaApiNativa_SemChave_ComSchemaEKeepAlive()
    {
        var h = new FakeHandler
        {
            Resposta = () => Json("{\"message\":{\"role\":\"assistant\",\"content\":" + Escapa(Saida) + "}}")
        };
        var p = new OllamaPlacaProvider(new HttpClient(h), new PlacaIAProvedorModel { provedor = "ollama", model = "qwen2.5vl" });

        var l = await p.LerAsync(Jpeg, null, CancellationToken.None);

        Assert.Equal("ABC1D23", l.placa);
        Assert.Equal("http://localhost:11434/api/chat", h.Req!.RequestUri!.ToString());
        Assert.Null(h.Req.Headers.Authorization);
        Assert.False((bool)h.Body!["stream"]!);
        Assert.Equal("10m", (string?)h.Body["keep_alive"]);
        Assert.Equal("object", (string?)h.Body["format"]!["type"]);
        Assert.Equal(Convert.ToBase64String(Jpeg), (string?)h.Body["messages"]![0]!["images"]![0]);
        Assert.Equal(0.0, (double)h.Body["options"]!["temperature"]!);
    }
    #endregion

    #region Recorte
    // Cena sintetica: fundo cinza, folhagem verde-escura e um retangulo verde puro tracejado.
    private static byte[] CenaComRetangulo(int w, int h, OpenCvSharp.Rect caixa, bool comRetangulo = true)
    {
        using var m = new OpenCvSharp.Mat(h, w, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(110, 110, 110));
        OpenCvSharp.Cv2.Rectangle(m, new OpenCvSharp.Rect(50, 40, 400, 120), new OpenCvSharp.Scalar(40, 120, 50), -1); // folhagem
        OpenCvSharp.Cv2.Rectangle(m, new OpenCvSharp.Rect(caixa.X + 12, caixa.Y + 12, caixa.Width - 24, caixa.Height - 24),
            new OpenCvSharp.Scalar(230, 230, 230), -1); // "placa" dentro da caixa
        if (comRetangulo)
        {
            // tracejado: 14 px de traco, 10 de falha, 3 px de espessura, verde puro (BGR 0,255,0)
            var verde = new OpenCvSharp.Scalar(0, 255, 0);
            for (int x = caixa.X; x < caixa.Right; x += 24)
            {
                int xf = Math.Min(x + 14, caixa.Right);
                OpenCvSharp.Cv2.Line(m, new OpenCvSharp.Point(x, caixa.Y), new OpenCvSharp.Point(xf, caixa.Y), verde, 3);
                OpenCvSharp.Cv2.Line(m, new OpenCvSharp.Point(x, caixa.Bottom), new OpenCvSharp.Point(xf, caixa.Bottom), verde, 3);
            }
            for (int y = caixa.Y; y < caixa.Bottom; y += 24)
            {
                int yf = Math.Min(y + 14, caixa.Bottom);
                OpenCvSharp.Cv2.Line(m, new OpenCvSharp.Point(caixa.X, y), new OpenCvSharp.Point(caixa.X, yf), verde, 3);
                OpenCvSharp.Cv2.Line(m, new OpenCvSharp.Point(caixa.Right, y), new OpenCvSharp.Point(caixa.Right, yf), verde, 3);
            }
        }
        OpenCvSharp.Cv2.ImEncode(".jpg", m, out var jpg);
        return jpg;
    }

    [Fact]
    public void Recorte_AchaORetanguloVerdeTracejadoIgnoraFolhagem_EAmplia()
    {
        var caixa = new OpenCvSharp.Rect(1500, 700, 220, 110);
        var jpg = CenaComRetangulo(2560, 1920, caixa);

        var recorte = PlacaIARecorte.Gerar(jpg, out var info);

        Assert.NotNull(recorte);
        using var m = OpenCvSharp.Cv2.ImDecode(recorte!, OpenCvSharp.ImreadModes.Color);
        // Ampliado: a caixa tinha 220 px; o recorte (com margem) chega a ~900 px no lado maior (tolera arredondamento).
        Assert.True(Math.Max(m.Width, m.Height) >= 890, $"recorte {m.Width}x{m.Height} ({info})");
        // E ainda e so a regiao da caixa (proporcao proxima de 2:1, nao a cena 4:3 inteira).
        Assert.InRange((double)m.Width / m.Height, 1.5, 2.6);
    }

    [Fact]
    public void Recorte_SemRetanguloVerde_DevolveNull()
    {
        var jpg = CenaComRetangulo(1280, 960, new OpenCvSharp.Rect(500, 400, 200, 100), comRetangulo: false);
        Assert.Null(PlacaIARecorte.Gerar(jpg, out var info));
        Assert.Contains("nao encontrado", info);
    }

    [Fact]
    public void Recorte_ImagemInvalida_DevolveNull()
    {
        Assert.Null(PlacaIARecorte.Gerar(new byte[] { 1, 2, 3 }, out var info));
        Assert.Contains("invalida", info);
    }

    [Fact]
    public async Task Servico_GeraORecorteEEnviaAoProvedor_ESemRecorteQuandoDesligado()
    {
        var jpg = CenaComRetangulo(2560, 1920, new OpenCvSharp.Rect(1500, 700, 220, 110));
        byte[]? recebido = null; bool chamou = false;
        var svc = Servico(_ => new ProvedorEspia((r) => { recebido = r; chamou = true; }));

        var com = await svc.VerificarAsync(Cfg(), jpg, "ABC1D23", null);
        Assert.True(chamou);
        Assert.NotNull(recebido);
        Assert.True(com.recorteUsado);

        recebido = null;
        var sem = await svc.VerificarAsync(Cfg(c => c.recortarRetanguloVerde = false), jpg, "ABC1D23", null);
        Assert.Null(recebido);
        Assert.False(sem.recorteUsado);
    }

    private sealed class ProvedorEspia : IPlacaVisionProvider
    {
        private readonly Action<byte[]?> espia;
        public ProvedorEspia(Action<byte[]?> espia) => this.espia = espia;
        public string Nome => "espia";
        public string Modelo => "m";
        public Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct, PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            espia(recorte);
            return Task.FromResult(Leitura("ABC1D23"));
        }
    }

    // Foto real (nao fica no repositorio): defina PLACAIA_TESTE_IMG com o caminho do jpg para rodar.
    [Fact]
    public void Recorte_FotoReal_AcertaAPlacaDentroDoRetangulo()
    {
        var caminho = Environment.GetEnvironmentVariable("PLACAIA_TESTE_IMG");
        if (string.IsNullOrEmpty(caminho) || !File.Exists(caminho)) { return; }

        var recorte = PlacaIARecorte.Gerar(File.ReadAllBytes(caminho), out var info);

        Assert.NotNull(recorte);
        using var m = OpenCvSharp.Cv2.ImDecode(recorte!, OpenCvSharp.ImreadModes.Color);
        Assert.InRange(m.Width, 500, 2000);
        Console.WriteLine("recorte real: " + info + " -> " + m.Width + "x" + m.Height);
        var saida = Environment.GetEnvironmentVariable("PLACAIA_TESTE_SAIDA");
        if (!string.IsNullOrEmpty(saida)) { File.WriteAllBytes(saida, recorte!); }
    }
    #endregion

    #region Servico
    private sealed class ProvedorFalso : IPlacaVisionProvider
    {
        private readonly Func<Task<PlacaIALeituraModel>> acao;
        public ProvedorFalso(string nome, Func<Task<PlacaIALeituraModel>> acao) { Nome = nome; this.acao = acao; }
        public string Nome { get; }
        public string Modelo => "m";
        public Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct, PlacaIAModo modo = PlacaIAModo.RetanguloVerde) => acao();
    }

    private static PlacaIAService Servico(Func<PlacaIAProvedorModel, IPlacaVisionProvider> fabrica) =>
        new(NullLogger<PlacaIAService>.Instance, null!) { FabricaProvider = fabrica };

    [Fact]
    public async Task Servico_PrimarioFalha_UsaFallback()
    {
        var cfg = Cfg(c =>
        {
            c.provedor = "anthropic";
            c.fallback = new PlacaIAProvedorModel { provedor = "ollama" };
        });
        var svc = Servico(p => p.provedor == "anthropic"
            ? new ProvedorFalso("anthropic", () => throw new HttpRequestException("503"))
            : new ProvedorFalso("ollama", () => Task.FromResult(Leitura("ABC1D23"))));

        var r = await svc.VerificarAsync(cfg, Jpeg, "ABC1D23", Detran());

        Assert.Equal(PlacaIAComparador.Confirmada, r.status);
        Assert.Equal("ollama", r.provedor);
        Assert.True(r.usouFallback);
    }

    [Theory]
    [InlineData("manter", false)]
    [InlineData("suprimir", true)]
    public async Task Servico_TodosFalham_SegueFalhaIA(string falhaIA, bool suprime)
    {
        var cfg = Cfg(c => c.falhaIA = falhaIA);
        var svc = Servico(_ => new ProvedorFalso("x", () => throw new HttpRequestException("fora do ar")));

        var r = await svc.VerificarAsync(cfg, Jpeg, "ABC1D23", Detran());

        Assert.Equal(PlacaIAComparador.NaoVerificada, r.status);
        Assert.Equal(suprime, r.suprimirAlerta);
        Assert.Contains("fora do ar", r.erro);
    }

    [Fact]
    public async Task Servico_Timeout_ViraNaoVerificada()
    {
        var cfg = Cfg(c => c.timeoutSeg = 1);
        var svc = Servico(_ => new ProvedorComToken());

        var r = await svc.VerificarAsync(cfg, Jpeg, "ABC1D23", null);

        Assert.Equal(PlacaIAComparador.NaoVerificada, r.status);
        Assert.Contains("timeout", r.erro);
    }

    private sealed class ProvedorComToken : IPlacaVisionProvider
    {
        public string Nome => "lento";
        public string Modelo => "m";
        public async Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct, PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new PlacaIALeituraModel();
        }
    }

    [Fact]
    public async Task Servico_ProvedorDesconhecido_ViraNaoVerificada()
    {
        var svc = new PlacaIAService(NullLogger<PlacaIAService>.Instance, new FabricaHttpFalsa());
        var r = await svc.VerificarAsync(Cfg(c => c.provedor = "inexistente"), Jpeg, "ABC1D23", null);
        Assert.Equal(PlacaIAComparador.NaoVerificada, r.status);
        Assert.Contains("desconhecido", r.erro);
    }

    private sealed class FabricaHttpFalsa : IHttpClientFactory
    {
        private readonly HttpMessageHandler handler;
        public FabricaHttpFalsa(HttpMessageHandler? handler = null) => this.handler = handler ?? new FakeHandler();
        public HttpClient CreateClient(string name) => new(handler);
    }

    [Fact]
    public async Task Servico_OpenRouter_UsaEnderecoDoOpenRouter_EChaveDoAmbiente()
    {
        var h = new FakeHandler
        {
            Resposta = () => Json("{\"choices\":[{\"message\":{\"content\":" + Escapa(Saida) + "}}]}")
        };
        var svc = new PlacaIAService(NullLogger<PlacaIAService>.Instance, new FabricaHttpFalsa(h));
        var cfg = Cfg(c => { c.provedor = "openrouter"; c.model = "google/gemini-2.5-flash"; c.apiKey = ""; });
        Environment.SetEnvironmentVariable(PlacaIAService.EnvApiKey, "chave-do-ambiente");
        try
        {
            var r = await svc.VerificarAsync(cfg, Jpeg, "ABC1D23", null);

            Assert.Equal(PlacaIAComparador.Confirmada, r.status);
            // Nunca pode cair no endereco da OpenAI com a chave do OpenRouter.
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", h.Req!.RequestUri!.ToString());
            Assert.Equal("chave-do-ambiente", h.Req.Headers.Authorization!.Parameter);
            Assert.Equal("google/gemini-2.5-flash", (string?)h.Body!["model"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlacaIAService.EnvApiKey, null);
        }
    }

    [Fact]
    public void Resolve_ChaveDoArquivoTemPrioridadeSobreOAmbiente()
    {
        Environment.SetEnvironmentVariable(PlacaIAService.EnvApiKey, "do-ambiente");
        try
        {
            var r = PlacaIAService.Resolve(new PlacaIAProvedorModel { provedor = "openrouter", apiKey = "do-arquivo" }, false);
            Assert.Equal("do-arquivo", r.apiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlacaIAService.EnvApiKey, null);
        }
    }
    #endregion
}
