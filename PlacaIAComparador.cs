using System.Globalization;
using System.Text;

namespace GatewayProxy.PlacaIA
{
    // Regras puras (sem rede) para decidir se a leitura da IA confirma a placa do LPR.
    public static class PlacaIAComparador
    {
        public const string Confirmada = "confirmada";
        public const string Divergente = "divergente";
        public const string Ilegivel = "ilegivel";
        public const string BaixaConfianca = "baixaConfianca";
        public const string NaoVerificada = "naoVerificada";
        // Leitura sem placa esperada (PlacaIAService.LerAsync): a IA leu uma placa completa e confiavel.
        public const string Lida = "lida";

        // Avalia uma leitura sem placa esperada: lida | ilegivel | baixaConfianca. Nunca suprime nada.
        public static PlacaIAResultModel AvaliarLeitura(PlacaIAJsonModel cfg, PlacaIALeituraModel leitura)
        {
            var r = new PlacaIAResultModel
            {
                placaIA = NormalizaPlaca(leitura.placa),
                legivel = leitura.legivel,
                confianca = leitura.confianca,
                veiculo = leitura.veiculo,
                tokensEntrada = leitura.tokensEntrada,
                tokensSaida = leitura.tokensSaida
            };
            if (!leitura.legivel || r.placaIA.Length == 0 || (leitura.placa ?? "").Contains('?'))
            {
                r.status = Ilegivel;
                r.motivo = "IA nao conseguiu ler a placa";
            }
            else if (leitura.confianca < cfg.minConfianca)
            {
                r.status = BaixaConfianca;
                r.motivo = $"IA com baixa confianca ({leitura.confianca:P0}) leu {r.placaIA}";
            }
            else
            {
                r.status = Lida;
                r.motivo = $"IA leu {r.placaIA}";
            }
            return r;
        }

        // Maiusculas, so letras e digitos (remove hifen/espacos).
        public static string NormalizaPlaca(string? placa)
        {
            if (string.IsNullOrWhiteSpace(placa)) { return ""; }
            var sb = new StringBuilder(placa.Length);
            foreach (var c in placa)
            {
                if (char.IsAsciiLetterOrDigit(c)) { sb.Append(char.ToUpperInvariant(c)); }
            }
            return sb.ToString();
        }

        public static PlacaIAResultModel Avaliar(PlacaIAJsonModel cfg, string placaLida, PlacaIALeituraModel leitura,
            VeiculoReferencia? referencia)
        {
            var r = new PlacaIAResultModel
            {
                placaLida = placaLida,
                placaIA = NormalizaPlaca(leitura.placa),
                legivel = leitura.legivel,
                confianca = leitura.confianca,
                veiculo = leitura.veiculo,
                tokensEntrada = leitura.tokensEntrada,
                tokensSaida = leitura.tokensSaida
            };
            var esperada = NormalizaPlaca(placaLida);
            bool igual = r.placaIA.Length > 0 && r.placaIA == esperada;

            // "?" marca caractere incerto na leitura crua (NormalizaPlaca o remove, entao testa antes).
            if (!leitura.legivel || r.placaIA.Length == 0 || (leitura.placa ?? "").Contains('?'))
            {
                r.status = Ilegivel;
                r.motivo = "IA nao conseguiu ler a placa";
            }
            else if (leitura.confianca < cfg.minConfianca)
            {
                r.status = BaixaConfianca;
                r.motivo = $"IA com baixa confianca ({leitura.confianca:P0}) leu {r.placaIA}";
            }
            else if (!igual)
            {
                r.status = Divergente;
                r.motivo = $"IA divergente: leu {r.placaIA}";
            }
            else
            {
                r.status = Confirmada;
                r.motivo = "IA confirmou a placa";
            }

            // So a leitura de OUTRA placa, com confianca alta, suprime. Ilegivel ou confianca baixa nao e
            // evidencia contra o alerta: ele segue, marcado para revisao (a menos que suprimirSeIlegivel).
            bool naoConfirmou = r.status == Ilegivel || r.status == BaixaConfianca;
            r.suprimirAlerta = r.status == Divergente || (naoConfirmou && cfg.suprimirSeIlegivel);
            if (naoConfirmou && !r.suprimirAlerta)
            {
                r.motivo += " (alerta mantido, revisar)";
            }

            // Veiculo (tipo/marca) contra a referencia (DETRAN-ES no GatewayOne). Informativo: com a placa confirmada, veiculo
            // diferente indica possivel placa clonada. Nao decide sozinho, porque tambem acontece quando o
            // LPR errou a placa; quem decide e a leitura da placa pela IA.
            if (referencia != null)
            {
                r.divergenciaVeiculo = VeiculoDiverge(leitura.veiculo, referencia, out var detalhe);
                r.detalheVeiculo = detalhe;
                if (r.divergenciaVeiculo)
                {
                    r.motivo += r.status == Confirmada
                        ? $" | veiculo divergente do {referencia.fonte} (possivel placa clonada): {detalhe}"
                        : $" | veiculo divergente do {referencia.fonte}: {detalhe}";
                    if (cfg.suprimirSeVeiculoDivergir) { r.suprimirAlerta = true; }
                }

                r.divergenciaCor = CorDiverge(leitura.veiculo?.cor, referencia.descCor);
                if (r.divergenciaCor)
                {
                    r.detalheVeiculo += $" | cor IA: {leitura.veiculo?.cor} | cor {referencia.fonte}: {referencia.descCor}";
                }

                // Regra intermediaria: a IA nao confirmou a placa E o veiculo (tipo/marca) E a cor divergem do cadastro.
                // Quase certo que o LPR leu a placa errada e o cadastro e de outro carro.
                if (cfg.suprimirSeNaoConfirmouEVeiculoDivergir && naoConfirmou && !r.suprimirAlerta
                    && r.divergenciaVeiculo && r.divergenciaCor)
                {
                    r.suprimirAlerta = true;
                    r.motivo = (r.motivo ?? "").Replace(" (alerta mantido, revisar)", "")
                        + $" | alerta suprimido: placa nao confirmada pela IA e veiculo e cor divergem do {referencia.fonte}";
                }
            }
            return r;
        }

        // Resultado quando nenhum provedor respondeu.
        public static PlacaIAResultModel NaoVerificadaResultado(PlacaIAJsonModel cfg, string placaLida, string erro)
        {
            bool suprimir = string.Equals(cfg.falhaIA, "suprimir", StringComparison.OrdinalIgnoreCase);
            return new PlacaIAResultModel
            {
                placaLida = placaLida,
                status = NaoVerificada,
                suprimirAlerta = suprimir,
                erro = erro,
                motivo = suprimir ? "IA indisponivel (alerta suprimido)" : "IA indisponivel (placa nao verificada)"
            };
        }

        #region Veiculo
        private enum Categoria { Desconhecida, Moto, Leve, Pesado }

        private static readonly Dictionary<string, string> Alias = new()
        {
            ["VW"] = "VOLKSWAGEN", ["GM"] = "CHEVROLET", ["CHEV"] = "CHEVROLET", ["MMC"] = "MITSUBISHI",
            ["MBENZ"] = "MERCEDES", ["BENZ"] = "MERCEDES", ["MERCEDESBENZ"] = "MERCEDES"
        };

        public static bool VeiculoDiverge(PlacaIAVeiculoModel? ia, VeiculoReferencia referencia, out string detalhe)
        {
            detalhe = $"IA: {ia?.tipo} {ia?.marca} {ia?.modelo} | {referencia.fonte}: {referencia.descTipo} {referencia.descMarca}".Trim();
            if (ia == null) { return false; }

            // Tipo: so compara categorias grossas (moto / leve / pesado); desconhecido nunca diverge.
            var cIa = CategoriaIA(ia.tipo);
            var cDet = CategoriaReferencia(referencia);
            if (cIa != Categoria.Desconhecida && cDet != Categoria.Desconhecida && cIa != cDet)
            {
                return true;
            }

            // Marca: alguma palavra (marca ou modelo da IA) precisa aparecer em descMarca ("VW/GOL 1.0").
            var tokensDet = Tokens(referencia.descMarca);
            var tokensIa = Tokens(ia.marca);
            tokensIa.UnionWith(Tokens(ia.modelo));
            if (tokensDet.Count > 0 && tokensIa.Count > 0 && !tokensIa.Overlaps(tokensDet))
            {
                return true;
            }
            return false;
        }

        // Cor da IA (branca, preta, prata...) contra a do cadastro (BRANCA, PRETA, PRATA...). So diz "diverge" quando
        // a diferenca e grosseira, porque a IA confunde tons vizinhos (prata/branca, prata/cinza, cinza/preta,
        // azul-escuro/preta). Cor desconhecida ou fora da lista nunca diverge.
        //  - neutras (escala claro -> escuro: branca 0, bege 0,5, dourada 1, prata 1, cinza 2, preta 3):
        //    diverge se a distancia for >= 2 (branca-cinza, branca-preta, prata-preta, bege-preta);
        //  - cromaticas: diverge se forem familias diferentes (vermelho/vinho/rosa/laranja, amarelo/marrom, azul, verde);
        //  - cromatica contra neutra: so diverge se a neutra for clara (branca, bege, dourada, prata).
        public static bool CorDiverge(string? corIA, string? corReferencia)
        {
            var a = ClasseCor(corIA);
            var b = ClasseCor(corReferencia);
            if (a == null || b == null) { return false; }
            if (a.Value.neutra && b.Value.neutra) { return Math.Abs(a.Value.escala - b.Value.escala) >= 2.0; }
            if (!a.Value.neutra && !b.Value.neutra) { return a.Value.familia != b.Value.familia; }
            var neutra = a.Value.neutra ? a.Value : b.Value;
            return neutra.escala <= 1.0;
        }

        private static (bool neutra, double escala, string familia)? ClasseCor(string? cor)
        {
            var t = Normaliza(cor);
            if (t.Length == 0) { return null; }
            // Prefixos cobrem o feminino/masculino/aumentativo: BRANC(A/O), PRET(A/O), PRATEAD(A/O), CINZ(A/ENTA)...
            if (t.StartsWith("BRANC")) { return (true, 0.0, ""); }
            if (t.StartsWith("BEGE")) { return (true, 0.5, ""); }
            if (t.StartsWith("DOURAD")) { return (true, 1.0, ""); }
            if (t.StartsWith("PRAT")) { return (true, 1.0, ""); }
            if (t.StartsWith("CINZ")) { return (true, 2.0, ""); }
            if (t.StartsWith("PRET")) { return (true, 3.0, ""); }
            if (t.StartsWith("VERMELH") || t.StartsWith("VINHO") || t.StartsWith("BORDO") || t.StartsWith("ROSA")
                || t.StartsWith("LARANJ")) { return (false, 0, "vermelho"); }
            if (t.StartsWith("AMAREL") || t.StartsWith("MARROM")) { return (false, 0, "amarelo"); }
            if (t.StartsWith("AZUL")) { return (false, 0, "azul"); }
            if (t.StartsWith("VERDE")) { return (false, 0, "verde"); }
            return null; // "outra", "fantasia", desconhecida
        }

        private static Categoria CategoriaIA(string? tipo)
        {
            var t = Normaliza(tipo);
            if (t.Length == 0) { return Categoria.Desconhecida; }
            if (t.Contains("MOTO")) { return Categoria.Moto; }
            if (t.Contains("CAMINHAO") || t.Contains("ONIBUS")) { return Categoria.Pesado; }
            if (t == "OUTRO") { return Categoria.Desconhecida; }
            return Categoria.Leve; // carro, suv, picape, van
        }

        private static Categoria CategoriaReferencia(VeiculoReferencia d)
        {
            if (d.codTipo == 4) { return Categoria.Moto; }
            var tokens = Normaliza(d.descTipo).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Por palavra inteira: "MOTOR CASA" nao e moto; "MICROONIBUS" nao e onibus (ambiguo com van).
            if (tokens.Any(t => t is "MOTO" or "MOTOCICLETA" or "MOTONETA" or "CICLOMOTOR")) { return Categoria.Moto; }
            if (tokens.Any(t => t is "CAMINHAO" or "ONIBUS" or "TRATOR" or "REBOQUE")) { return Categoria.Pesado; }
            if (tokens.Any(t => t is "AUTOMOVEL" or "CAMIONETA" or "CAMINHONETE" or "UTILITARIO")) { return Categoria.Leve; }
            return Categoria.Desconhecida;
        }

        private static HashSet<string> Tokens(string? s)
        {
            var set = new HashSet<string>();
            foreach (var t in Normaliza(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (t.Length < 2) { continue; } // "I/" (importado), "M"
                set.Add(Alias.TryGetValue(t, out var canon) ? canon : t);
            }
            return set;
        }

        // Sem acentos, maiusculas, so letras/digitos separados por espaco.
        private static string Normaliza(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) { return ""; }
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) { continue; }
                sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
            }
            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        #endregion
    }
}
