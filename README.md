# Anime Upscale Studio

Aplicativo Windows em C# / WPF para ampliar vídeos com Anime4K ou AnimeJaNai e
exportar MKV em AV1 NVENC 10-bit. Interface em português, fila, piloto de qualidade,
estimativas locais e recuperação de blocos interrompidos.

## Requisitos

- Windows 10/11 x64 e driver NVIDIA atualizado.
- GPU NVIDIA com AV1 NVENC. A distribuição atual inclui builders TensorRT para
  RTX 40 (SM89) e RTX 50 (SM120).
- Espaço para modelos, engines, saída e blocos temporários. AnimeJaNai usa GPU;
  tempo e consumo variam por modelo, resolução e computador.

Anime4K gera 3840×2160. AnimeJaNai dobra a resolução: 720p→1440p, 1080p→4K.
Comece com o piloto de 15 segundos em V3.1 Balanced. Compact FP16 é experimental.
A primeira execução pode levar minutos preparando a engine na sua GPU.

## Instalação e atualizações

O empacotamento usa **Velopack 1.2.0**, com .NET autocontido. Quando houver uma
release publicada, o instalador estará em [Releases](https://github.com/ImCarL6/anime-upscale-studio/releases).
Código no GitHub não significa que um instalador já foi publicado.

A versão instalada verifica atualizações ao abrir e a cada seis horas. Em
**Atualizações e novidades**, veja o changelog e baixe uma versão nova.
O reinício depende de confirmação e só é permitido sem fila/piloto ativo.
Falhas de rede não bloqueiam o processamento. O modo de desenvolvimento não
se atualiza automaticamente.

## Desenvolvimento

Instale o .NET SDK 10 e execute na raiz:

```powershell
dotnet restore src/Anime4KEncoder/Anime4KEncoder.csproj -r win-x64 --locked-mode
dotnet build src/Anime4KEncoder/Anime4KEncoder.csproj -c Release -r win-x64 --no-restore
dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj
```

A compilação não exige baixar modelos. Para processar vídeos ou gerar um
instalador completo, prepare as dependências descritas em
[DEPENDENCIES.md](docs/DEPENDENCIES.md).

```powershell
pwsh -File scripts/Build-Installer.ps1 -RuntimeSource 'D:\StudioRuntime' -Version 0.1.0
```

O script confere os hashes, compila, gera Setup/portable/pacotes Velopack e
feed em `artifacts/Releases`. Não faz upload. Veja
[publicação e atualização](docs/RELEASING.md), [arquitetura](docs/ARCHITECTURE.md)
e [privacidade](docs/PRIVACY.md).

## Escopo e validação

O pipeline de vídeo foi testado em RTX 4070 Ti. A presença de SM120 no runtime
não substitui o piloto em uma RTX 5070. Não há benchmark dessa placa.
Vídeos, históricos e resultados locais não são publicados neste repositório.

Consulte [CHANGELOG.md](CHANGELOG.md) e [componentes de terceiros](THIRD_PARTY_NOTICES.md).
Não foi escolhida uma licença geral para o código próprio; as licenças de
terceiros continuam aplicáveis. Repositório público não concede por si só uma licença.
