# Publicação e privacidade

O repositório contém código, shaders com licença, scripts de build, hashes de
dependências e documentação. Não contém arquivos de mídia, relatórios locais,
históricos, logs, engines TensorRT, timing caches, backups ou credenciais.

Modelos, DLLs e executáveis são insumos locais ignorados pelo Git. Um instalador
contém os binários necessários: esses arquivos não são secretos nem ficam
invisíveis para quem instala. `.gitignore` evita versionamento acidental;
não protege dados já publicados nem substitui revisão do commit.

O aplicativo não envia vídeos, nomes de arquivos ou históricos ao projeto.
A consulta de atualizações faz requisições normais ao GitHub, que recebe dados
de conexão como IP. Logs ficam localmente e podem conter caminhos de vídeos,
nome da GPU e versão do driver. Revise-os antes de abrir um issue público.

Não inclua PATs, certificados de assinatura ou senhas no código. Publicações
devem usar autenticação do mantenedor ou GitHub Actions com permissões limitadas.
Atualizações não instalam drivers NVIDIA.
