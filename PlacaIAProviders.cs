using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GatewayProxy.PlacaIA
{
    public abstract class PlacaIAProviderBase : IPlacaVisionProvider
    {
        protected readonly HttpClient http;
        protected readonly PlacaIAProvedorModel cfg;

        protected PlacaIAProviderBase(HttpClient http, PlacaIAProvedorModel cfg)
        {
            this.http = http;
            this.cfg = cfg;
        }

        public abstract string Nome { get; }
        public string Modelo => string.IsNullOrWhiteSpace(cfg.model) ? ModeloPadrao : cfg.model!;
        protected abstract string ModeloPadrao { get; }
        protected abstract string BaseUrlPadrao { get; }
        protected string BaseUrl => (string.IsNullOrWhiteSpace(cfg.baseUrl) ? BaseUrlPadrao : cfg.baseUrl!).TrimEnd('/');

        public abstract Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde);

        // Imagem inteira primeiro (veiculo), recorte ampliado depois (placa).
        protected static IEnumerable<string> ImagensBase64(byte[] jpeg, byte[]? recorte)
        {
            yield return Convert.ToBase64String(jpeg);
            if (recorte != null) { yield return Convert.ToBase64String(recorte); }
        }

        // POST JSON. Em erro HTTP lanca com o status e um trecho do corpo (nunca a chave nem a imagem).
        protected async Task<JsonNode> PostJsonAsync(string url, JsonObject body, Action<HttpRequestMessage> headers,
            CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            headers(req);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var trecho = txt.Length > 300 ? txt.Substring(0, 300) : txt;
                throw new HttpRequestException($"{Nome} HTTP {(int)resp.StatusCode}: {trecho}");
            }
            return JsonNode.Parse(txt) ?? throw new InvalidOperationException($"{Nome}: resposta vazia");
        }
    }

    // Anthropic Messages API (raw HTTP). Nao envia temperature: os modelos Claude atuais
    // (Sonnet 5.5 / Opus 5.5) rejeitam valores diferentes do padrao com 400.
    public class AnthropicPlacaProvider : PlacaIAProviderBase
    {
        public AnthropicPlacaProvider(HttpClient http, PlacaIAProvedorModel cfg) : base(http, cfg) { }
        public override string Nome => "anthropic";
        protected override string ModeloPadrao => "claude-opus-5-5";
        protected override string BaseUrlPadrao => "https://api.anthropic.com";

        public override async Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            var conteudo = new JsonArray();
            foreach (var b64 in ImagensBase64(jpeg, recorte))
            {
                conteudo.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = "image/jpeg",
                        ["data"] = b64
                    }
                });
            }
            conteudo.Add(new JsonObject { ["type"] = "text", ["text"] = PlacaIAPrompt.Montar(recorte != null, modo) });
            var body = new JsonObject
            {
                ["model"] = Modelo,
                // Folga para o raciocinio adaptativo; a resposta em si tem ~60 tokens.
                ["max_tokens"] = 4096,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = conteudo })
            };
            if (!string.IsNullOrWhiteSpace(cfg.esforco))
            {
                body["output_config"] = new JsonObject { ["effort"] = cfg.esforco };
            }
            var r = await PostJsonAsync(BaseUrl + "/v1/messages", body, req =>
            {
                req.Headers.Add("x-api-key", cfg.apiKey ?? "");
                req.Headers.Add("anthropic-version", "2023-06-01");
            }, ct).ConfigureAwait(false);

            if ((string?)r["stop_reason"] == "refusal")
            {
                throw new InvalidOperationException("anthropic: modelo recusou a requisicao (refusal)");
            }
            // content pode trazer blocos "thinking" antes do "text".
            string? texto = null;
            if (r["content"] is JsonArray blocos)
            {
                foreach (var b in blocos)
                {
                    if ((string?)b?["type"] == "text") { texto = (string?)b["text"]; }
                }
            }
            var leitura = PlacaIAPrompt.Parse(texto);
            leitura.tokensEntrada = (int?)r["usage"]?["input_tokens"];
            leitura.tokensSaida = (int?)r["usage"]?["output_tokens"];
            return leitura;
        }
    }

    // Google Gemini (generateContent).
    public class GeminiPlacaProvider : PlacaIAProviderBase
    {
        public GeminiPlacaProvider(HttpClient http, PlacaIAProvedorModel cfg) : base(http, cfg) { }
        public override string Nome => "gemini";
        protected override string ModeloPadrao => "gemini-2.5-flash";
        protected override string BaseUrlPadrao => "https://generativelanguage.googleapis.com";

        public override async Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            var gen = new JsonObject { ["responseMimeType"] = "application/json" };
            if (cfg.temperatura.HasValue) { gen["temperature"] = cfg.temperatura.Value; }
            var partes = new JsonArray();
            foreach (var b64 in ImagensBase64(jpeg, recorte))
            {
                partes.Add(new JsonObject
                {
                    ["inline_data"] = new JsonObject { ["mime_type"] = "image/jpeg", ["data"] = b64 }
                });
            }
            partes.Add(new JsonObject { ["text"] = PlacaIAPrompt.Montar(recorte != null, modo) });
            var body = new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = partes }),
                ["generationConfig"] = gen
            };
            var url = $"{BaseUrl}/v1beta/models/{Uri.EscapeDataString(Modelo)}:generateContent";
            var r = await PostJsonAsync(url, body, req => req.Headers.Add("x-goog-api-key", cfg.apiKey ?? ""), ct)
                .ConfigureAwait(false);
            // Bloqueio por seguranca: nao ha candidates.
            var partesResp = r["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            if (partesResp == null)
            {
                throw new InvalidOperationException("gemini: resposta sem candidates (" +
                    (string?)r["promptFeedback"]?["blockReason"] + ")");
            }
            var sb = new StringBuilder();
            foreach (var p in partesResp) { sb.Append((string?)p?["text"]); }
            var leitura = PlacaIAPrompt.Parse(sb.ToString());
            leitura.tokensEntrada = (int?)r["usageMetadata"]?["promptTokenCount"];
            leitura.tokensSaida = (int?)r["usageMetadata"]?["candidatesTokenCount"];
            return leitura;
        }
    }

    // Protocolo OpenAI /chat/completions: OpenAI, DeepSeek, xAI, OpenRouter, vLLM, LM Studio...
    public class OpenAiCompatPlacaProvider : PlacaIAProviderBase
    {
        public OpenAiCompatPlacaProvider(HttpClient http, PlacaIAProvedorModel cfg) : base(http, cfg) { }
        public override string Nome => "openai";
        protected override string ModeloPadrao => "gpt-4o";
        protected override string BaseUrlPadrao => "https://api.openai.com/v1";

        public override async Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            var conteudo = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = PlacaIAPrompt.Montar(recorte != null, modo) } };
            foreach (var b64 in ImagensBase64(jpeg, recorte))
            {
                conteudo.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + b64 }
                });
            }
            var body = new JsonObject
            {
                ["model"] = Modelo,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = conteudo })
            };
            if (cfg.temperatura.HasValue) { body["temperature"] = cfg.temperatura.Value; }
            var r = await PostJsonAsync(BaseUrl + "/chat/completions", body, req =>
            {
                if (!string.IsNullOrWhiteSpace(cfg.apiKey))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.apiKey);
                }
            }, ct).ConfigureAwait(false);
            var texto = (string?)r["choices"]?[0]?["message"]?["content"];
            var leitura = PlacaIAPrompt.Parse(texto);
            leitura.tokensEntrada = (int?)r["usage"]?["prompt_tokens"];
            leitura.tokensSaida = (int?)r["usage"]?["completion_tokens"];
            return leitura;
        }
    }

    // Ollama local (API nativa /api/chat): saida estruturada via "format" (JSON Schema).
    public class OllamaPlacaProvider : PlacaIAProviderBase
    {
        public OllamaPlacaProvider(HttpClient http, PlacaIAProvedorModel cfg) : base(http, cfg) { }
        public override string Nome => "ollama";
        // Sem modelo padrao confiavel: o operador escolhe um modelo de visao ja baixado (ollama pull).
        protected override string ModeloPadrao => "llama3.2-vision";
        protected override string BaseUrlPadrao => "http://localhost:11434";

        public override async Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            var options = new JsonObject();
            var imagens = new JsonArray();
            foreach (var b64 in ImagensBase64(jpeg, recorte)) { imagens.Add(b64); }
            if (cfg.temperatura.HasValue) { options["temperature"] = cfg.temperatura.Value; }
            var body = new JsonObject
            {
                ["model"] = Modelo,
                ["stream"] = false,
                ["format"] = PlacaIAPrompt.Schema(),
                ["keep_alive"] = string.IsNullOrWhiteSpace(cfg.keepAlive) ? "10m" : cfg.keepAlive,
                ["options"] = options,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = PlacaIAPrompt.Montar(recorte != null, modo),
                    ["images"] = imagens
                })
            };
            var r = await PostJsonAsync(BaseUrl + "/api/chat", body, req =>
            {
                // Ollama local nao exige chave; atras de proxy reverso pode exigir.
                if (!string.IsNullOrWhiteSpace(cfg.apiKey))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.apiKey);
                }
            }, ct).ConfigureAwait(false);
            var leitura = PlacaIAPrompt.Parse((string?)r["message"]?["content"]);
            leitura.tokensEntrada = (int?)r["prompt_eval_count"];
            leitura.tokensSaida = (int?)r["eval_count"];
            return leitura;
        }
    }
}
