# Dependências do pacote completo

O Git contém o app e shaders. Para montar um pacote offline, `RuntimeSource`
deve apontar a uma pasta com a estrutura e os hashes de
[`scripts/runtime-files.json`](../scripts/runtime-files.json):

```
tools/ffmpeg/{ffmpeg.exe,ffprobe.exe}
tools/videojanai/inference/{aji_encode.exe,aji.dll,aji_trt.dll,...}
2x_AnimeJaNai_HD_V3_ModelsOnly/*.onnx
licenses/*
```

Esse manifesto fixa o conjunto já validado. O build recusa arquivos ausentes ou
alterados e copia apenas a lista, nunca toda a pasta do computador de origem.
Os seis ONNX ativos incluem V3.1 Balanced/Performance/Sharp1 e Compact strong-FP16.

## Origem

- [FFmpeg Gyan builds](https://www.gyan.dev/ffmpeg/builds/): executáveis da build
  `2026-06-26-git-d66e84695b`; bibliotecas compartilhadas do bundle de inferência.
- [animejanai-inference](https://github.com/the-database/animejanai-inference):
  binários aji provenientes do bundle VideoJaNai 2.1.0.
- [vs-mlrt v16.test1](https://github.com/AmusementClub/vs-mlrt/releases/tag/v16.test1):
  TensorRT 11.0, CUDA runtime e Visual C++ app-local; builder SM89 e SM120.
- [AnimeJaNai](https://github.com/the-database/AnimeJaNai): modelos. Os Compact
  strong-FP16 são conversões locais, não downloads idênticos aos originais FP32.
- [Anime4K](https://github.com/bloc97/Anime4K): shaders com licença em `shaders/LICENSE`.

Não é prometido bootstrap integral por download: alguns insumos foram convertidos
localmente e o manifesto não substitui a origem nem os termos de redistribuição.
O mantenedor deve fornecer o conjunto validado para builds completos. A compilação
do código e CI não precisam desses arquivos pesados.

Para mudar dependências, validar a nova toolchain, revisar licenças/origem e
atualizar deliberadamente o manifesto. Nunca atualizar hashes apenas para
contornar um erro de verificação. Não distribuir engines de outra GPU.
