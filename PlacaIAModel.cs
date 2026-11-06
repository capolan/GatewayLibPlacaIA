using System.Text.Json.Serialization;

namespace GatewayProxy.PlacaIA
{
    // Configuracao de UM provedor de IA de visao (principal ou fallback).
    public class PlacaIAProvedorModel
    {
        // anthropic | gemini | openai | ollama
        // "openai" fala o protocolo /chat/completions: serve tambem DeepSeek, xAI, OpenRouter,
        // vLLM etc. bastando trocar baseUrl e model.
        public string provedor { get; set; } = "anthropic";
        public string? model { get; set; }
        // Vazio no Ollama. Nunca logar.
        public string? apiKey { get; set; }
        // Vazio = endereco padrao do provedor (Ollama: http://localhost:11434).
        public string? baseUrl { get; set; }
        public int timeoutSeg { get; set; } = 15;
        // Anthropic: output_config.effort (vazio = nao envia). Os modelos Claude atuais nao aceitam
        // temperature diferente do padrao, entao o provedor anthropic nunca envia temperature.
        public string? esforco { get; set; } = "low";
        // Gemini/OpenAI/Ollama: null = nao envia o campo (alguns modelos de raciocinio rejeitam).
        public double? temperatura { get; set; } = 0;
        // Ollama: quanto tempo o modelo fica carregado na memoria apos a chamada.
        public string keepAlive { get; set; } = "10m";
    }

    // Secao "PlacaIA" do oneConfig.json.
    public class PlacaIAJsonModel : PlacaIAProvedorModel
    {
        public bool ativo { get; set; } = false;
        // Confianca minima (0..1) informada pela IA para aceitar a leitura.
        public double minConfianca { get; set; } = 0.8;
        // O que fazer se a IA estiver fora/estourar timeout: "manter" (alerta sai marcado como
        // nao verificado) ou "suprimir".
        public string falhaIA { get; set; } = "manter";
        // Placa ilegivel ou lida com confianca abaixo de minConfianca: false (padrao) mantem o alerta,
        // marcado para revisao humana; true suprime. Placa pequena/borrada e comum e a IA erra ou
        // hesita, entao suprimir por isso derrubaria alertas reais (caso FindFaces 18885325).
        // So uma leitura de OUTRA placa com confianca alta suprime sempre.
        public bool suprimirSeIlegivel { get; set; } = false;
        // Marca/tipo do veiculo divergente do DETRAN-ES: so informa (false) ou tambem suprime (true).
        public bool suprimirSeVeiculoDivergir { get; set; } = false;
        // Regra intermediaria (padrao false): suprime quando a IA NAO confirmou a placa (ilegivel ou confianca
        // baixa) E o veiculo diverge do cadastro em tipo/marca E tambem na cor. Cobre o LPR que leu uma placa
        // errada e achou um furto/roubo de outro carro (caso OVI2A40: cadastro Cruze preto, na foto um sedan
        // prata), sem derrubar o alerta quando a IA leu a placa certa (possivel clone) nem quando ha so uma
        // divergencia isolada de marca (a IA erra marca com mais frequencia que a cor grosseira). Cor
        // desconhecida de qualquer lado nunca conta como divergencia.
        public bool suprimirSeNaoConfirmouEVeiculoDivergir { get; set; } = false;
        // true = manda so o recorte da placa (cam.plate) em vez da imagem inteira (menos dados pessoais).
        public bool somenteRecorte { get; set; } = false;
        // Detecta o retangulo verde na imagem e manda tambem um recorte ampliado dele (2a imagem) para a IA
        // ler a placa. Sem retangulo na imagem, segue so com a imagem inteira.
        public bool recortarRetanguloVerde { get; set; } = true;
        // Provedor alternativo tentado quando o principal falha (erro/timeout/resposta invalida).
        public PlacaIAProvedorModel? fallback { get; set; }
    }

    // Como a imagem enviada deve ser lida pela IA.
    public enum PlacaIAModo
    {
        // A placa de interesse esta dentro de um retangulo verde desenhado na imagem (GatewayOne).
        RetanguloVerde = 0,
        // Sem marcacao: ler a placa do veiculo principal da cena (ParkREST e outros).
        PlacaPrincipal = 1
    }

    // Veiculo de referencia (ex.: cadastro do DETRAN-ES) contra o qual a descricao da IA e comparada.
    // Neutro de proposito: a lib nao conhece o modelo de nenhum consumidor.
    public class VeiculoReferencia
    {
        // Codigo do tipo de veiculo; 4 = moto (codigo do DETRAN-ES). Null se nao houver.
        public int? codTipo { get; set; }
        // Ex.: "AUTOMOVEL", "MOTOCICLETA", "CAMINHAO".
        public string? descTipo { get; set; }
        // Ex.: "VW/GOL 1.0".
        public string? descMarca { get; set; }
        // Ex.: "PRETA", "PRATA", "BRANCA". Opcional: sem cor a regra de supressao por cor nao dispara.
        public string? descCor { get; set; }
        // Nome da fonte, usado nos textos de motivo/detalhe.
        public string fonte { get; set; } = "DETRAN-ES";
    }

    public class PlacaIAVeiculoModel
    {
        // carro, suv, picape, van, caminhao, onibus, moto, outro
        public string? tipo { get; set; }
        public string? marca { get; set; }
        public string? modelo { get; set; }
        // Cor principal da carroceria: branca, preta, prata, cinza, vermelha, azul, verde, amarela, marrom,
        // bege, laranja, dourada, rosa, vinho, outra. Vazio = a IA nao soube dizer.
        public string? cor { get; set; }
    }

    // Leitura crua devolvida pelo provedor (ja parseada).
    public class PlacaIALeituraModel
    {
        public string? placa { get; set; }
        public bool legivel { get; set; }
        public double confianca { get; set; }
        public PlacaIAVeiculoModel? veiculo { get; set; }
        // Consumo informado pelo provedor (null quando ele nao informa).
        public int? tokensEntrada { get; set; }
        public int? tokensSaida { get; set; }
    }

    // Resultado final da verificacao, depois da comparacao com a placa lida pelo LPR.
    public class PlacaIAResultModel
    {
        public string? placaLida { get; set; }
        public string? placaIA { get; set; }
        public bool legivel { get; set; }
        public double confianca { get; set; }
        public PlacaIAVeiculoModel? veiculo { get; set; }
        // confirmada | divergente | ilegivel | baixaConfianca | naoVerificada
        public string status { get; set; } = "naoVerificada";
        public bool suprimirAlerta { get; set; }
        public bool divergenciaVeiculo { get; set; }
        // A cor descrita pela IA e claramente outra que a do cadastro (so quando as duas sao conhecidas).
        public bool divergenciaCor { get; set; }
        public string? detalheVeiculo { get; set; }
        public string? motivo { get; set; }
        public string? provedor { get; set; }
        public string? modelo { get; set; }
        public long ms { get; set; }
        public int? tokensEntrada { get; set; }
        public int? tokensSaida { get; set; }
        // Foi enviado o recorte ampliado do retangulo verde junto com a imagem inteira.
        public bool recorteUsado { get; set; }
        public string? erro { get; set; }
        [JsonIgnore]
        public bool usouFallback { get; set; }
    }
}
