# Arquitetura

- `src/Anime4KEncoder`: WPF .NET 10; gerenciamento da fila, probes, pilotos e remux.
- Anime4K: FFmpeg / Vulkan / libplacebo / shaders → AV1 NVENC.
- AnimeJaNai: aji_encode / TensorRT → pipe → FFmpeg AV1 NVENC, em blocos de
  aproximadamente cinco minutos, com sobreposição temporal e validação.
- FFprobe valida identidade de streams, resolução, frames e timestamps;
  o áudio é validado por decodificação. Originais são preservados.
- Fingerprint da toolchain impede reutilizar blocos/pilotos de configurações incompatíveis.

## Instalação versus dados

Velopack instala em `%LOCALAPPDATA%/ImCarL6.AnimeUpscaleStudio`.
Essa pasta é gerenciada pelo updater; não deve guardar dados persistentes.
`StoragePaths` reconhece o marcador `sq.version` e coloca dados em
`%LOCALAPPDATA%/AnimeUpscaleStudio/UserData`, fora da árvore substituída:

- `data`: históricos e fingerprints;
- `logs`: diagnósticos locais;
- `validation/pilots`: previews e relatórios;
- `cache/shaders`: shaders combinados;
- `models/<hash-do-manifesto>`: cópias dos ONNX e engines/timing caches locais.

`runtime-manifest.json` é idêntico entre releases que alteram somente o app.
Assim, essas releases preservam as engines. Mudanças nas dependências criam um
namespace novo, sem apagar o anterior. Os blocos continuam no destino do vídeo.
No modo sem `sq.version`, os dados permanecem junto à raiz portátil.

Não há importação automática dos históricos da antiga pasta portátil. Ela é
preservada. A instalação começa com dados próprios e novas medições.

## Atualização

Bootstrap Velopack é a primeira chamada de Main. Autoaplicação no startup está
desligada e um mutex impede duas instâncias normais. A consulta lê releases
públicas estáveis do repositório, sem token embutido. Download é explícito;
aplicação revalida a fila após o download e antes do reinício.
Fechar o app com processamento ativo exige cancelar e aguardar a finalização.
A fila aberta não é persistida para o reinício: a confirmação informa isso.
Notas remotas são exibidas como texto, sem HTML executável.
