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
- exportação/importação `.pzasm-pack` leve por padrão (configuração, IDs, permissões, ordem, assets, publicação e automação, com download posterior das fontes), além de um modo completo deduplicado explícito com fontes fixadas e build idênticos;
- exportação/importação `.pzasm-servers` criptografada com AES-256-GCM para conexões remotas, incluindo segredos API/RCON e chaves SSH opcionais, com nova criptografia local no destino e substituição explícita de conflitos;
- snapshots privados SHA-256 para fixar exatamente as versões de origem;
- importação por Workshop ID e inclusão das dependências `require=` disponíveis;
- catálogo Workshop interno com pesquisa, ordenação, tags, prévias, paginação, acesso direto por ID e cesta de seleção persistente entre páginas com remoção individual;
- mesmo seletor visual para packs e listas `WorkshopItems`/`Mods` de servidores locais ou dedicados, mantendo a edição bruta;
- instalação portátil gerenciada automaticamente do SteamCMD diretamente da Valve no Windows e Linux na primeira operação que precisar dele, com preparação manual opcional pela interface ou `pzasm steamcmd install`;
- downloads anônimos de fontes públicas do Workshop, separados da conta autenticada de publicação;
- Bundle sem reescrever manifests, Lua, scripts, mapas ou assets;
- Strict Fusion com desduplicação de arquivos idênticos e relatório de conflitos;
- descrição do Workshop, manifesto público e lockfile completos;
- registro de autores, licenças, permissões e provas privadas não publicadas;
- status e avisos de permissão apenas informativos, sem bloquear build, publicação ou automação; o administrador mantém o controle e a responsabilidade;
- janela de conexão multilíngue opcional, ativada por padrão, com lista completa, versões declaradas, perfis PZ e revisões fixadas;
- criação e atualizações posteriores do mesmo item Workshop;
- espaço de projeto moderno e responsivo, com grupos mais claros, cartões de permissões recolhidos por padrão, seis idiomas persistentes e temas claro/escuro (claro por padrão);
- progresso detalhado da importação do Workshop com item e fase atuais, contador, porcentagem, resultado da análise e erros recuperáveis;
- assistente de prioridade de mapas baseado em `map.info`, dependências `lots=`, conflitos de células `.lotheader`, arrastar e soltar e edição bruta de `Map=`;
- editor guiado do servidor para identidade, acesso, RCON, sessão, backups e conteúdo, além do editor INI completo; na inicialização local, a tabela SQLite `whitelist` é lida e a senha inicial de `admin` só é solicitada quando a conta realmente não existe;
- redetecção dinâmica por `zombie.network.GameServer` e `-servername`, inclusive quando o servidor foi iniciado antes do gerenciador; processos `-coop` são separados dos servidores dedicados, o cliente gráfico sozinho é ignorado e instâncias duplicadas de um perfil são sinalizadas como conflito. A visão em abas oferece logs legíveis de `server-console.txt` ou `coop-console.txt`, pesquisa e filtros por severidade, stdout/stderr limitado e sanitizado, rede, RCON e console de comandos/respostas;
- progresso detalhado e cancelável para publicação, autenticação SteamCMD e atualização de mods, com saída ao vivo e limite de tempo;
- UI local e CLI headless para Windows e Linux;
- daemon `automation run` com bloqueios entre processos.

Consulte [transferências portáteis](docs/PORTABLE-TRANSFERS.md) para conteúdo, criptografia, substituição atômica, deduplicação de disco, limpeza, limites e uso por CLI.

### Comandos do projeto e atualizações

Construir, Atualizar mods e Publicar aparecem como os comandos principais do projeto. Ações sensíveis sempre usam uma janela de confirmação integrada à interface, nunca diálogos nativos do navegador. O autor e o detentor dos direitos são preenchidos a partir do `mod.info` de cada fonte quando disponíveis e continuam editáveis. Cada mod pode ser excluído da atualização global e atualizado individualmente; seu snapshot permanece fixado até que a atualização individual seja solicitada explicitamente.

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
O SteamCMD é baixado, extraído de forma controlada e inicializado na pasta do gerenciador quando é necessário pela primeira vez. O painel, a aba Distribuição e a CLI também podem prepará-lo ou reinstalá-lo imediatamente. Fontes públicas do Project Zomboid são baixadas anonimamente por padrão; somente a publicação exige a conta editora.

O SteamCMD baixa IDs conhecidos, mas não oferece pesquisa completa. O catálogo interno enumera resultados públicos da Steam Community, obtém metadados públicos e entrega a seleção ao SteamCMD. A publicação agendada não exige um servidor local; a coordenação RCON é opcional.

## Fluxo recomendado

1. Crie um projeto no modo **Bundle**.
2. Adicione mods detectados ou importe um Workshop ID.
3. Registre autor e autorização de cada origem.
4. Revise a ordem dos mods e mapas.
5. Construa e examine `pack.lock.json` e `server-config.txt`.
6. Deixe o gerenciador preparar o SteamCMD automaticamente (ou prepare-o imediatamente em Distribuição), configure a conta editora, use **Conectar / renovar sessão** e publique primeiro como privado.
7. Teste em um servidor de staging antes da produção.

## CLI headless

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd login --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Servidor principal"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Cada projeto representa um pacote global independente. Nada é atualizado automaticamente até o administrador ativar a automação. Unidades systemd de referência estão em `deploy/systemd/`.

## Docker, Coolify e acesso seguro

O contêiner de produção inclui o gerenciador web, o agendador, o cliente SSH, as bibliotecas Linux de 32 bits do SteamCMD e sua instalação automática. Todas as páginas administrativas exigem uma conta. Administradores gerenciam usuários e revogam sessões; operadores gerenciam pacotes e servidores sem acesso às contas.

No Windows, `just docker-secret-setup` protege com DPAPI a senha inicial e uma chave de dados independente fora do repositório; senhas RCON e tokens de API são criptografados com AES-GCM. No Linux, use um `.env` com modo `600` ou um gerenciador de segredos externo. No Coolify, configure `PZASM_ADMIN_PASSWORD` e uma chave estável `PZASM_DATA_ENCRYPTION_KEY` com pelo menos 32 caracteres aleatórios como variáveis protegidas; o Compose as monta como arquivos secretos somente leitura. Publique a porta `5160` por HTTPS e preserve sempre o volume `pzasm-data`. Consulte [Docker e Coolify](docs/DOCKER-COOLIFY.md).

## SteamCMD e servidores remotos

O Pine Hosting possui um backend de API separado. Uma chave de API e o identificador do servidor permitem reutilizar os editores INI, SandboxVars e Lua, implantação de packs, console, controle do processo e backups do provedor sem SSH. Restaurações e fresh start exigem o servidor parado e oferecem um backup de segurança antes da operação. Consulte [Pine Hosting provider](docs/PINE-HOSTING.md).

A senha Steam e o código Steam Guard são enviados ao SteamCMD pela entrada padrão somente durante a solicitação; o PZASM não os coloca na linha de comando nem os armazena. O SteamCMD mantém seu próprio token na pasta portátil para o agendamento. Se a sessão expirar ou faltar um segredo, a publicação termina imediatamente com uma explicação em vez de aguardar silenciosamente. A interface mostra a saída ao vivo e permite cancelar o processo externo.

Um perfil remoto pode usar apenas RCON: estado autenticado, console, `save`, `quit` e coordenação sem SSH. Se systemd, Docker, o painel ou a hospedagem reiniciar o Project Zomboid depois de `quit`, o PZASM publica primeiro e então solicita o reinício seguro por RCON. SSH continua opcional para ler ou alterar o INI ou iniciar explicitamente o processo do jogo. O PZASM nunca reinicia o VPS ou servidor dedicado inteiro.

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
