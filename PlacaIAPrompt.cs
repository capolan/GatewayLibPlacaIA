using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GatewayProxy.PlacaIA
{
    // Prompt unico e parse da resposta, compartilhados por todos os provedores.
    public static class PlacaIAPrompt
    {
        public const string Texto =
            "You verify Brazilian license plates in a surveillance photo.\n" +
            "The photo shows a vehicle whose license plate is enclosed in a GREEN rectangle.\n" +
            "1. Read ONLY the plate inside the green rectangle. Ignore every other plate or text in the image.\n" +
            "2. Brazilian plates have 7 characters: old format AAA9999 or Mercosul format AAA9A99. " +
            "Return them uppercase, without hyphen or spaces.\n" +
            "3. Do not guess. If any character is blurred, cut off or ambiguous (for example 0/O, 1/I, 8/B, 5/S), " +
            "set \"legivel\" to false and use \"?\" for the uncertain characters.\n" +
            "4. \"confianca\" is your confidence from 0.0 to 1.0 that the whole plate is exactly right.\n" +
            "5. Also describe the vehicle that carries that plate: \"tipo\" is one of carro, suv, picape, van, " +
            "caminhao, onibus, moto, outro; \"marca\" and \"modelo\" are your best identification " +
            "(empty string if you cannot tell).\n" +
            "Answer with ONLY one JSON object, no markdown, no commentary:\n" +
            "{\"placa\":\"ABC1D23\",\"legivel\":true,\"confianca\":0.95," +
            "\"veiculo\":{\"tipo\":\"carro\",\"marca\":\"\",\"modelo\":\"\"}}";

        // Sem retangulo: le a placa do veiculo principal da cena (ParkREST e outros).
        public const string TextoPlacaPrincipal =
            "You read Brazilian license plates in a photo taken at a parking lot or gate.\n" +
            "1. Read the license plate of the main vehicle in the photo (the closest, most prominent one). " +
            "Ignore other vehicles and any other text.\n" +
            "2. Brazilian plates have 7 characters: old format AAA9999 or Mercosul format AAA9A99. " +
            "Return them uppercase, without hyphen or spaces.\n" +
            "3. Do not guess. If any character is blurred, cut off or ambiguous (for example 0/O, 1/I, 8/B, 5/S), " +
            "set \"legivel\" to false and use \"?\" for the uncertain characters. If there is no plate, use an empty string.\n" +
            "4. \"confianca\" is your confidence from 0.0 to 1.0 that the whole plate is exactly right.\n" +
            "5. Also describe the vehicle that carries that plate: \"tipo\" is one of carro, suv, picape, van, " +
            "caminhao, onibus, moto, outro; \"marca\" and \"modelo\" are your best identification " +
            "(empty string if you cannot tell).\n" +
            "Answer with ONLY one JSON object, no markdown, no commentary:\n" +
            "{\"placa\":\"ABC1D23\",\"legivel\":true,\"confianca\":0.95," +
            "\"veiculo\":{\"tipo\":\"carro\",\"marca\":\"\",\"modelo\":\"\"}}";

        // Com recorte, o modelo recebe duas imagens: a cena (para o veiculo) e o recorte ampliado (para a placa).
        // O recorte so existe no modo RetanguloVerde.
        public static string Montar(bool comRecorte, PlacaIAModo modo = PlacaIAModo.RetanguloVerde)
        {
            if (modo == PlacaIAModo.PlacaPrincipal) { return TextoPlacaPrincipal; }
            return comRecorte
                ? "Two images are attached. Image 1 is the full scene: use it to identify the vehicle. " +
                  "Image 2 is a magnified crop around the green rectangle: use it to read the plate characters.\n" + Texto
                : Texto;
        }

        // JSON Schema equivalente (usado onde o provedor aceita saida estruturada: Ollama).
        public static JsonObject Schema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["placa"] = new JsonObject { ["type"] = "string" },
                ["legivel"] = new JsonObject { ["type"] = "boolean" },
                ["confianca"] = new JsonObject { ["type"] = "number" },
                ["veiculo"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tipo"] = new JsonObject { ["type"] = "string" },
                        ["marca"] = new JsonObject { ["type"] = "string" },
                        ["modelo"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("tipo", "marca", "modelo")
                }
            },
            ["required"] = new JsonArray("placa", "legivel", "confianca", "veiculo")
        };

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Extrai o objeto JSON do texto (tolera cerca ```json e texto em volta).
        public static PlacaIALeituraModel Parse(string? texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
            {
                throw new InvalidOperationException("Resposta da IA vazia");
            }
            int ini = texto.IndexOf('{');
            int fim = texto.LastIndexOf('}');
            if (ini < 0 || fim <= ini)
            {
                throw new InvalidOperationException("Resposta da IA sem JSON");
            }
            var json = texto.Substring(ini, fim - ini + 1);
            PlacaIALeituraModel? r;
            try
            {
                using var doc = JsonDocument.Parse(json);
                r = Ler(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("JSON da IA invalido: " + ex.Message);
            }
            return r;
        }

        // Leitura tolerante: tipos trocados (confianca como string, legivel como "true") nao derrubam.
        private static PlacaIALeituraModel Ler(JsonElement e)
        {
            var r = new PlacaIALeituraModel();
            foreach (var p in e.EnumerateObject())
            {
                switch (p.Name.ToLowerInvariant())
                {
                    case "placa":
                        r.placa = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                        break;
                    case "legivel":
                        r.legivel = p.Value.ValueKind == JsonValueKind.True ||
                            (p.Value.ValueKind == JsonValueKind.String &&
                             bool.TryParse(p.Value.GetString(), out var b) && b);
                        break;
                    case "confianca":
                        double c = 0;
                        if (p.Value.ValueKind == JsonValueKind.Number) { c = p.Value.GetDouble(); }
                        else if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            double.TryParse(p.Value.GetString()?.Replace(',', '.'), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out c);
                        }
                        // Alguns modelos devolvem 0-100 em vez de 0-1.
                        if (c > 1.0 && c <= 100.0) { c /= 100.0; }
                        r.confianca = Math.Clamp(c, 0, 1);
                        break;
                    case "veiculo":
                        if (p.Value.ValueKind == JsonValueKind.Object)
                        {
                            r.veiculo = JsonSerializer.Deserialize<PlacaIAVeiculoModel>(p.Value.GetRawText(), JsonOpts);
                        }
                        break;
                }
            }
            return r;
        }
    }
}
