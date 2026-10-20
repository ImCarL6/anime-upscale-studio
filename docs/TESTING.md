# Testes

## Blocos dinâmicos (0.1.7)

`ReleaseChecks` cobre 1/3/5 minutos, CFR fracionário, cobertura exata sem lacunas ou
sobreposição útil, mudança nos dois sentidos, preservação do prefixo, plano misto
persistido e adaptação de caches fixos da 0.1.6. Layouts inconsistentes são rejeitados.

`Test-DynamicSegments.ps1 -Package <runtime-isolado>` gera uma fixture CFR de oito
minutos com 480 frames. Troca o seletor nas fronteiras para selecionar um bloco de
cinco minutos, um de um minuto e uma cauda no modo equilibrado. Libera o ritmo após
a seleção de cada fronteira para não transformar o teste em benchmark do modo
reduzido. Exige validação final de frames, timestamps e áudio e limpeza após sucesso.
`-CancelAndResume` cancela após os dois primeiros blocos, simula um parcial inacabado
e exige retomada sem reescrever os blocos concluídos. É um teste de pipeline com GPU,
não um benchmark de episódio real.

## Controle ao vivo (0.1.6)

`ResourceChecks` recebe opcionalmente o caminho do FFmpeg: testa CPU 35→75→máximo→35
no mesmo processo, emergência de memória ainda ativa em máximo, comandos de
cadência e identidade de 200 frames/timestamps. Testa máximo→limitado→máximo.
As dependências de criptografia do projeto de console são fixadas em versões
corrigidas; o aplicativo WPF usa as dependências fornecidas pelo runtime desktop.

`Test-BackgroundV2.ps1 -Package <prévia> -FixturePath <mídia-sintética> -LiveSwitch`
verifica seletor habilitado em processamento, confirma os três comandos no mesmo
bloco/tentativa e exige validação final, limpeza e marcador de histórico misto.

## Retomada e proteção

- `dotnet run --project tests/ResourceChecks/ResourceChecks.csproj`: CPU nativa 35%/75%, herança, remoção do limite artificial de memória, reserva, pressão transitória/sustentada e orçamento de duas retomadas.
- `Test-BackgroundV2.ps1 -Package <prévia> -FixturePath <fixture-sintética>`: fila real, cadência, saída validada e limpeza após sucesso.
- Acrescentar `-ForceRecovery`: interrompe apenas o Job Object da tentativa, simulando pressão sem esgotar memória; verifica espera mínima de 30s, retomada mais lenta e resultado final.
- Acrescentar `-ForceRecovery -CancelRecovery`: cancela manualmente durante a espera e verifica ausência de retomada.
- `-ProfileCode V3-Compact-Sharp1` verifica o perfil Compact alternativo com a mesma fixture sintética.

Estas verificações não comprovam responsividade com episódio completo e navegador aberto.

## Limpeza e padrões de abertura (0.1.5)

`ReleaseChecks` verifica remoção após sucesso, preservação em falha/cancelamento,
isolamento entre trabalhos, limpeza repetida, remoção do diretório pai vazio e
rejeição de caminhos fora de `.anime-upscale-temp`.
`dotnet run --project tests/DefaultsChecks/DefaultsChecks.csproj -- <runtime-completo>`
instancia a janela WPF sem iniciar encoders e confere AnimeJaNai, V3.1 Balanced Sharp1, CQ 18 e modo Equilibrado.
Isso não substitui uma conversão completa com validação final e limpeza em uso real.

## Sessão e integridade

`Test-SessionQueue.ps1 -Package <runtime-isolado> -FixturePath <mídia-sintética>`
verifica inclusão durante a fila, processamento automático dos novos itens, recusa
antecipada de arquivo truncado, restauração de ordem/estados/pastas/tempo e retorno
de trabalho interrompido como pendente. Também aceita legendas que terminam depois
do vídeo. Use uma distribuição isolada: o teste grava sua própria sessão sintética.

`ReleaseChecks` cobre EOF prematuro com código de saída zero, duração inconsistente,
legendas estendidas e recuperação do arquivo de sessão pela cópia de segurança.

## Sem GPU / CI

`dotnet run --project tests/ReleaseChecks/ReleaseChecks.csproj` usa apenas fixtures
sintéticas para verificar dados persistentes, reaproveitamento de engines em update
do app, invalidação por toolchain e preservação de históricos. Não executa inferência.

## Com GPU

Scripts em `src/Anime4KEncoder/Test-*.ps1` incluem checks de fingerprint, metadata,
piloto e pipeline segmentado. Use PowerShell em STA para os que instanciam WPF.
Os scripts de integração precisam de uma distribuição completa e fixtures locais;
nenhum vídeo ou relatório privado é incluído no Git. Passe `InputPath` explicitamente
nos testes de vídeo. Não interprete paths de saída/validation como fixtures distribuídas.

`Test-ResumeValidation.ps1` gera mídia sintética; compara idiomas, resolução 2x,
frames, timestamps e áudio. O teste de piloto realiza três trechos de cinco segundos
e verifica persistência das projeções. Testes antigos carregam DLLs por reflexão;
execute-os no ambiente de desenvolvimento com suas dependências resolvidas.

## Instalador e updater

Build de Setup.exe e validação de bootstrap são smoke tests. Uma publicação pública
deve também passar pela instalação e por um ciclo real entre duas versões em Windows
de teste, conforme docs/RELEASING.md. Separar claramente esses resultados no relatório.

O teste de feed usa o TestVelopackLocator oficial e os pacotes gerados:

```powershell
dotnet run --project tests/UpdateFeedChecks/UpdateFeedChecks.csproj -- artifacts/Releases artifacts/feed-check
```

Confere consulta, changelog, download, ausência de update para versão atual e
rejeição de pacote corrompido. Não instala/aplica nem simula uma RTX 5070.

## Fila e comparador

`Test-QueueFeatures.ps1 -Package <pasta-completa>` verifica reordenação da coleção,
bloqueio durante processamento e ações de saída somente após sucesso. Com
`-PilotReport <pilot-report.json> -RenderPath <imagem.png>`, verifica o comparador
usando imagens reais do piloto e renderiza sua interface para inspeção.

`Test-PilotEndToEnd.ps1` também exige pares de comparação, tamanhos iguais e acesso
pela interface. ReleaseChecks testa offsets não contíguos da origem, índices do
piloto concatenado e amostras curtas/ímpares. As imagens são PNG RGB de inspeção;
não são métricas de qualidade nem uma referência de apresentação HDR.
