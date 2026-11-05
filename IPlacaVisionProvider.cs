
namespace GatewayProxy.PlacaIA
{
    // Um adaptador por protocolo de API (anthropic, gemini, openai-compativel, ollama).
    public interface IPlacaVisionProvider
    {
        string Nome { get; }
        string Modelo { get; }

        // Le a placa dentro do retangulo verde e descreve o veiculo. NAO recebe a placa lida pelo
        // LPR de proposito: dizer o que se espera ler induz o modelo a confirmar (vies).
        // Lanca excecao em erro de rede/HTTP/resposta invalida; o PlacaIAService trata.
        // recorte: JPEG ampliado em volta do retangulo verde (PlacaIARecorte), ou null. Quando existe, vai
        // como 2a imagem: a cena inteira continua sendo necessaria para descrever o veiculo.
        Task<PlacaIALeituraModel> LerAsync(byte[] jpeg, byte[]? recorte, CancellationToken ct,
            PlacaIAModo modo = PlacaIAModo.RetanguloVerde);
    }
}
