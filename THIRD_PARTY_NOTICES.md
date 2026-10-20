# Componentes de terceiros

| Componente | Origem / termos |
|---|---|
| Velopack 1.2.0 | https://github.com/velopack/velopack — MIT |
| .NET | https://github.com/dotnet/runtime — licenças incluídas pelo publish |
| Anime4K | https://github.com/bloc97/Anime4K — `shaders/LICENSE` |
| FFmpeg | https://ffmpeg.org/legal.html — builds GPLv3 usadas no runtime |
| TensorRT / CUDA | https://docs.nvidia.com/deeplearning/tensorrt/latest/reference/sla.html e https://docs.nvidia.com/cuda/eula/index.html |
| Visual C++ runtime | https://learn.microsoft.com/en-us/visualstudio/releases/2022/redistribution |
| AnimeJaNai / aji | https://github.com/the-database/AnimeJaNai e https://github.com/the-database/animejanai-inference |

This software contains source code provided by NVIDIA Corporation.

O controle de recursos ao vivo usa NetMQ 4.0.4.4, distribuído em DLL separada:
https://github.com/zeromq/netmq — LGPLv3, conforme o arquivo
COPYING.LESSER do projeto. Dependências gerenciadas são registradas em
packages.lock.json. AsyncIO 0.1.69 e NaCl.Net 0.1.13 usam MPL 2.0. As licenças e os endereços das fontes acompanham o instalador em `licenses/managed/`.

O repositório não redistribui binários/modelos. O pacote completo inclui avisos
em `licenses/`. Esses avisos não concedem direitos adicionais. Antes de publicar
instaladores públicos, conferir os termos dos binários aji, dos modelos convertidos
e as obrigações de disponibilização de código-fonte das builds GPL utilizadas.
Não atribuir ao código próprio uma licença de terceiros sem decisão do autor.
