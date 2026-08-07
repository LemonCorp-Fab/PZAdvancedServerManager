# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager (PZASM) é um gerenciador local para Project Zomboid e seu servidor dedicado. Ele distribui um conjunto coerente de mods por meio de **um único Workshop ID**, para que o servidor sincronize o pacote em vez de cada item de origem separadamente.

> Estado: versão funcional para Windows e Linux. Bundle, snapshots fixados, catálogo Workshop interno, SteamCMD, agendamento autônomo ou coordenado, aviso de conexão, gerenciamento de servidor e CLI headless estão implementados. Teste sempre a primeira publicação em um item privado.

## Veredito técnico

Um item do Workshop pode conter várias pastas em `mods/`, cada uma com seu próprio `mod.info` e `id=`:

```ini
WorkshopItems=ID_UNICO_DO_PACOTE
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIX
```

Servidor e clientes comparam somente a versão do item Workshop global. Depois, os Mod IDs internos controlam o carregamento. As verificações normais de Lua e checksum continuam ativas.

O modo recomendado é **Bundle**, que preserva pastas e Mod IDs originais. **Strict Fusion** cria um único Mod ID, mas rejeita qualquer colisão entre arquivos diferentes.

Leia o [estudo completo de arquitetura](docs/ARCHITECTURE.pt-BR.md).

## Recursos

- detecção do jogo, servidor dedicado, bibliotecas Steam, SteamCMD e mods locais/Workshop;
- suporte às estruturas Build 41/42 e pastas de versão compatíveis;
- projetos independentes e reabertos posteriormente, cada um com GUID e Workshop ID próprios;
- snapshots privados SHA-256 para fixar exatamente as versões de origem;
- importação por Workshop ID e inclusão das dependências `require=` disponíveis;
- catálogo Workshop interno com pesquisa, ordenação, tags, prévias, paginação, acesso direto por ID e cesta de seleção persistente entre páginas com remoção individual;
- mesmo seletor visual para packs e listas `WorkshopItems`/`Mods` de servidores locais ou dedicados, mantendo a edição bruta;
- instalação portátil do SteamCMD em um clique diretamente da Valve no Windows e Linux, também com `pzasm steamcmd install`;
- downloads anônimos de fontes públicas do Workshop, separados da conta autenticada de publicação;
- Bundle sem reescrever manifests, Lua, scripts, mapas ou assets;
- Strict Fusion com desduplicação de arquivos idênticos e relatório de conflitos;
- descrição do Workshop, manifesto público e lockfile completos;
- registro de autores, licenças, permissões e provas privadas não publicadas;
- janela de conexão opcional, ativada por padrão;
- criação e atualizações posteriores do mesmo item Workshop;
- espaço de projeto moderno com abas, configurações guiadas e controles avançados de valores exatos;
- assistente de prioridade de mapas baseado em `map.info`, dependências `lots=`, conflitos de células `.lotheader`, arrastar e soltar e edição bruta de `Map=`;
- editor guiado do servidor para identidade, acesso, RCON, sessão, backups e conteúdo, além do editor INI completo;
- encerramento RCON com `save`/`quit` e reinício coordenado;
- UI local e CLI headless para Windows e Linux;
- daemon `automation run` com bloqueios entre processos.

## Início

Para compilar, instale o [SDK .NET 9](https://dotnet.microsoft.com/download/dotnet/9.0). Os artefatos independentes da CI não precisam do runtime .NET instalado.

```powershell
Start-PZASM.cmd
```

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

A UI escuta localmente em `http://localhost:5160`. Use `--data-root <caminho>` para compartilhar o diretório de dados entre a UI e o CLI.
O SteamCMD pode ser instalado pelo painel ou pela aba Distribuição. Fontes públicas do Project Zomboid são baixadas anonimamente por padrão; somente a publicação exige a conta editora.

O SteamCMD baixa IDs conhecidos, mas não oferece pesquisa completa. O catálogo interno enumera resultados públicos da Steam Community, obtém metadados públicos e entrega a seleção ao SteamCMD. A publicação agendada não exige um servidor local; a coordenação RCON é opcional.

## Fluxo recomendado

1. Crie um projeto no modo **Bundle**.
2. Adicione mods detectados ou importe um Workshop ID.
3. Registre autor e autorização de cada origem.
4. Revise a ordem dos mods e mapas.
5. Construa e examine `pack.lock.json` e `server-config.txt`.
6. Instale o SteamCMD em um clique, configure a conta editora e publique primeiro como privado.
7. Teste em um servidor de staging antes da produção.

## CLI headless

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Servidor principal"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Cada projeto representa um pacote global independente. Nada é atualizado automaticamente até o administrador ativar a automação. Unidades systemd de referência estão em `deploy/systemd/`.

## Direitos e responsabilidade

PZASM não concede direitos sobre os mods incluídos. A [política oficial de mods](https://projectzomboid.com/blog/modding-policy/) exige permissões adequadas e uma lista completa para pacotes públicos ou não listados. O Steam também exige o aceite do [acordo do Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/).

O criador e publicador do pacote é o único responsável por permissões, licenças, créditos e conteúdo de terceiros. LemonCorp e colaboradores do PZASM não são responsáveis por pacotes criados ou publicados pelos usuários.

## Desenvolvimento

O repositório inclui um `Justfile` multiplataforma. Instale o [just](https://github.com/casey/just) e execute:

```text
just                 # listar todas as receitas
just check           # verificar formatação, compilar Release e testar
just build           # compilar toda a solução
just test            # executar todos os testes
just run-ui           # iniciar a UI e abrir o navegador
just run-cli help     # executar um comando CLI
just automation      # iniciar o agendador headless
just publish          # publicar para o sistema atual
just publish-all      # publicar win-x64 e linux-x64
```

As variáveis `CONFIGURATION` e `PUBLISH_DIR` alteram os padrões `Release` e `publish`. As receitas também aceitam argumentos adicionais.

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

Não exponha a porta do PZASM à Internet. A interface é uma ferramenta de administração local sem autenticação de rede.
