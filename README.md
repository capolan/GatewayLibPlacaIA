# GatewayLibPlacaIA (GatewayProxy.PlacaIA)

Le e confere placas brasileiras com uma IA de visao. Um adaptador por protocolo cobre os provedores:

| `provedor` | Protocolo | Observacao |
|---|---|---|
| `anthropic` (ou `claude`) | Anthropic Messages API | nao envia `temperature`; usa `esforco` (`output_config.effort`) |
| `gemini` (ou `google`) | `generateContent` | |
| `openai`, `openrouter`, `deepseek`, `xai` | `/chat/completions` | `baseUrl` padrao por provedor (OpenRouter: `https://openrouter.ai/api/v1`) |
| `ollama` | `/api/chat` nativo | modelos locais; saida JSON por schema; `keepAlive` |

Consumidores: **GatewayOne** (confere o alerta de furto/roubo do DETRAN-ES) e **ParkREST** (referencia + DI, sem uso ainda).
Referencia por `ProjectReference` ao clone em `Documents\VS2026\GatewayLibPlacaIA`, como as outras libs.

## Duas operacoes

```csharp
// 1) Conferir a placa que o LPR leu (GatewayOne). A IA NAO recebe a placa esperada (evita vies de confirmacao).
var r = await svc.VerificarAsync(cfg, jpeg, placaLida, referencia: veiculoDoDetran, processId);
if (r.suprimirAlerta) { /* a IA leu OUTRA placa com confianca >= minConfianca */ }

// 2) Ler sem placa esperada (leitor de reserva, ex.: ParkREST). Nunca lanca por falha do provedor.
var l = await svc.LerAsync(jpeg);              // modo PlacaPrincipal
if (l.status == PlacaIAComparador.Lida) { var placa = l.placaIA; }
```

`PlacaIAModo`:
- `RetanguloVerde` (padrao em `VerificarAsync`): a placa de interesse esta num retangulo verde desenhado na imagem. Com `recortarRetanguloVerde=true` a lib acha o retangulo, recorta com margem e amplia (~900 px), e manda **duas imagens** (cena inteira para o veiculo, recorte para os caracteres).
- `PlacaPrincipal` (padrao em `LerAsync`): sem marcacao; le a placa do veiculo principal. Nao gera recorte.

## Status

`PlacaIAResultModel.status`:

| status | Significado | `suprimirAlerta` |
|---|---|---|
| `confirmada` | IA leu a mesma placa | nao |
| `divergente` | IA leu **outra** placa, legivel, com confianca >= `minConfianca` | **sim** |
| `ilegivel` | IA nao conseguiu ler (ou ha `?`) | so se `suprimirSeIlegivel` |
| `baixaConfianca` | leu, mas abaixo de `minConfianca` | so se `suprimirSeIlegivel` |
| `naoVerificada` | IA fora do ar / timeout / resposta invalida | conforme `falhaIA` (`manter`\|`suprimir`) |
| `lida` | (so `LerAsync`) placa completa e confiavel | nunca |

So uma leitura de **outra** placa com confianca alta suprime. Ilegivel/baixa confianca nao e evidencia contra o alerta.
Modelos Claude informam confianca ~0,6 mesmo acertando; por isso o padrao em producao e `minConfianca = 0.5`.

O veiculo descrito pela IA (tipo/marca/modelo) e comparado a uma `VeiculoReferencia` (cadastro do DETRAN-ES etc.).
Divergencia e **informativa** (`divergenciaVeiculo`, possivel placa clonada) e so suprime com `suprimirSeVeiculoDivergir`.

## Configuracao (`PlacaIAJsonModel`)

Mesmos nomes da secao `PlacaIA` do `oneConfig.json` do GatewayOne.

```json
"PlacaIA": {
  "ativo": false,
  "provedor": "openrouter",
  "model": "anthropic/claude-haiku-5.5",
  "apiKey": "",
  "baseUrl": "",
  "timeoutSeg": 15,
  "esforco": "low",
  "temperatura": 0,
  "keepAlive": "10m",
  "minConfianca": 0.5,
  "falhaIA": "manter",
  "suprimirSeIlegivel": false,
  "suprimirSeVeiculoDivergir": false,
  "somenteRecorte": false,
  "recortarRetanguloVerde": true,
  "fallback": { "provedor": "ollama", "model": "qwen2.5vl", "timeoutSeg": 60 }
}
```

- `apiKey` vazia: usa a variavel de ambiente `PLACAIA_API_KEY` (`PLACAIA_FALLBACK_API_KEY` para o fallback). **Nao ponha chave em arquivo versionado.**
- `fallback`: provedor tentado quando o principal falha (erro, timeout, resposta invalida).
- `somenteRecorte` e interpretado pelo consumidor (GatewayOne manda so o recorte da placa em vez da imagem inteira, para enviar menos dados pessoais).

## Uso

ParkREST / qualquer app com `IConfiguration` (le `IOptionsMonitor`, entao recarrega a config):

```csharp
builder.Services.AddPlacaIA(builder.Configuration.GetSection("PlacaIA"));
// ...
if (svc.Ativo) { var r = await svc.LerAsync(jpeg, processId); }
```

GatewayOne (config estatica `OneConfig.PlacaIA`): `services.AddPlacaIAServico();` e passa a config em cada chamada.

## Custo e privacidade

Medido via OpenRouter, por chamada: Gemini 2.5 Flash ~US$ 0,0003, Haiku 5.5 ~US$ 0,001, Sonnet 5.5 ~US$ 0,013.
A **imagem inteira vai a um terceiro** (pode conter pessoas): documente isso para o cliente; `somenteRecorte` reduz.
A chave e a imagem nunca vao para o log.

## Dependencias

HttpClient/Options/Logging do `Microsoft.Extensions` e OpenCvSharp (so para o recorte; os runtimes win e linux-x64 vem junto).
Sem ONNX Runtime, OpenVINO ou modelos locais.

## Testes

```
dotnet test tests/GatewayProxy.PlacaIA.Tests
```

Opcional, com foto real (nao fica no repositorio): `PLACAIA_TESTE_IMG=<jpg com retangulo verde>` e `PLACAIA_TESTE_SAIDA=<recorte.jpg>`.
