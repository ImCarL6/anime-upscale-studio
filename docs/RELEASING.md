# Instalador, changelog e auto-update com Velopack

## Convenções

- SDK NuGet e CLI `vpk`: **1.2.0**, fixados juntos.
- ID: `ImCarL6.AnimeUpscaleStudio`; EXE: `Anime4KEncoder.exe`.
- Canal Windows: `win`; fonte: releases públicas estáveis deste repositório.
- Versionamento semântico, crescente. A integração inicial gerou um candidato `0.1.0`; só criar
  tag/release correspondente depois de validar e aprovar os artefatos reais.
  A versão atual do código está no csproj; candidatos locais não são releases publicadas.

## Gerar

```powershell
pwsh -File scripts/Build-Installer.ps1 -RuntimeSource 'D:\StudioRuntime' -Version 0.1.0 -ReleaseNotes 'docs\release-notes\0.1.0.md'
```

O script instala vpk localmente se necessário, valida dependências por SHA-256,
restaura o lockfile, publica .NET self-contained e gera os artefatos em
`artifacts/Releases`. `ReleaseNotes` aceita Markdown específico da versão;
se omitido, usa CHANGELOG.md. Atualize também o Version do csproj para manter
o build de desenvolvimento coerente com a release.

Conserve o pacote completo anterior na pasta de saída: Velopack usa esse pacote
para criar deltas. Arquivos avulsos de modelos ou binários não vão para o Git.
O primeiro instalador é grande; alterações somente no app podem ter deltas menores.

## Validar antes do upload

1. Compilar e executar os checks de persistência. Conferir manifesto e segredos.
2. Instalar em Windows de teste com GPU compatível. Confirmar atalhos, startup,
   dados fora de `current`, piloto dos dois motores e desinstalação.
3. Gerar uma segunda versão em uma pasta de feed de teste, instalar a anterior
   e verificar download/aplicação/reinício, preservação de dados e indisponibilidade
   durante processamento. Simular rede indisponível e download interrompido.
4. Conferir versão e changelog. Preservar os artefatos anteriores.
5. Revisar redistribuição dos componentes e assinar o instalador quando houver
   certificado. Sem assinatura, o Windows pode mostrar editor desconhecido.

## Publicar

Criar inicialmente uma **draft release** no GitHub com tag `vX.Y.Z`, título,
notas da versão e os arquivos da pasta de saída: Setup.exe, full.nupkg,
delta.nupkg (quando houver), `releases.win.json`, portable ZIP e SHA256SUMS.txt.
Usar os nomes gerados por Velopack. Não publicar somente o Setup.exe: o updater
precisa do feed e do pacote completo. Cada release deve conter apenas um pacote
full e um delta da versão correspondente.

Depois da revisão, publicar a draft como release estável. Drafts e prereleases
não aparecem para os clientes configurados para estável. Não substituir bytes
de uma versão já publicada: gerar outra versão. Tokens de publicação ficam no
gerenciador de credenciais/Secrets, nunca no código nem dentro do instalador.

O workflow CI apenas compila e testa o código. Não envia automaticamente os
binários pesados nem publica releases. Código enviado ao GitHub não ativa updates
até existir uma release estável válida com os artefatos Velopack.

## Referências oficiais

- [WPF e bootstrap](https://docs.velopack.io/getting-started/csharp)
- [Check, download e aplicação](https://docs.velopack.io/integrating/overview)
- [Layout Windows e preservação](https://docs.velopack.io/packaging/operating-systems/windows)
- [Deltas](https://docs.velopack.io/packaging/deltas)

O app desabilita auto-apply no startup para impedir reinício implícito. O usuário
confirma a aplicação. Uma atualização malsucedida deve ser diagnosticada antes
de anunciar rollback automático: este projeto não implementa rollback de saúde.
