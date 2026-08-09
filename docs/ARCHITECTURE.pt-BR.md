# Arquitetura e estudo de viabilidade

[English](ARCHITECTURE.md) · [Français](ARCHITECTURE.fr.md) · [Español](ARCHITECTURE.es.md) · [Deutsch](ARCHITECTURE.de.md) · [Português (Brasil)](ARCHITECTURE.pt-BR.md) · [简体中文](ARCHITECTURE.zh-CN.md)

## Conclusão

Project Zomboid pode carregar vários mods lógicos de um único item Workshop:

```text
um PublishedFileId do Workshop
└── mods/
    ├── ModA/          → mod.info: id=ModA
    ├── ModB/          → mod.info: id=ModB
    └── PZASM_Notice/  → mod.info: id=PZASM_Notice_SUFFIX
```

O jogo vê vários **Mod IDs**, mas somente **um Workshop ID para sincronizar**. Isso elimina o descompasso entre itens de origem independentes sem os riscos de fundir fisicamente todos os arquivos.

## Verificação cliente/servidor

A versão local 42.20.2 analisada primeiro compara Workshop IDs e timestamps; depois carrega `Mods=` por Mod ID. Mods lógicos dentro do mesmo item não recebem timestamps Workshop separados.

As verificações normais, incluindo `DoLuaChecksum`, continuam ativas. Esse comportamento deve ser testado novamente após grandes atualizações do jogo.

## Estrutura e conflitos

```text
steamapps/workshop/content/108600/<WorkshopId>/
└── mods/<PastaLogica>/
    ├── mod.info
    ├── media/
    ├── common/mod.info + media/
    └── 42.x/mod.info + media/
```

`media` pode conter Lua, scripts, mapas, texturas, modelos, animações, sons, rádios, traduções e UI. Dois mods podem reutilizar globals Lua, IDs de scripts, células de mapa, nomes de recursos ou chaves de tradução. Apenas renomear caminhos não corrige referências internas.

## Modos

**Bundle**, recomendado, preserva pastas e Mod IDs originais em um Workshop ID e mantém a maior compatibilidade.

**Strict Fusion**, avançado, gera `PZASM_Pack_<suffix>`, combina o conteúdo efetivo, desduplica arquivos idênticos e bloqueia qualquer colisão diferente. Serve apenas para conjuntos controlados e testados.

## Projetos e versões fixadas

Cada projeto tem um GUID imutável e seu próprio `publishedfileid`. O valor `0` cria um item; o SteamCMD grava o novo ID e o PZASM o mantém para atualizações posteriores.

Ao adicionar uma origem, o PZASM cria um snapshot privado e calcula seu SHA-256. Os builds usam essa cópia fixada, não o cache mutável do Steam. A atualização explícita substitui snapshots de forma atômica. `pack.lock.json` descreve exatamente o conteúdo entregue.

## Publicação e servidor

O [guia do Steamworks Workshop](https://partner.steamgames.com/doc/features/workshop/implementation) documenta criação e atualização com `workshop_build_item`.

A publicação é incremental em dois níveis. O PZASM calcula separadamente as impressões digitais do conteúdo entregue, dos metadados e da preview, omitindo do VDF as dimensões inalteradas. Em seguida, SteamCMD e Steam comparam o manifesto enviado ao anterior e transferem somente os chunks ausentes. O PZASM nunca baixa novamente o pacote depois do upload.

Um resultado “sem alterações” exige as três impressões digitais locais e uma nova leitura pela API pública dos handles remotos de conteúdo e preview, tamanho, horário de atualização, título, descrição e visibilidade. Se qualquer prova estiver indisponível ou desatualizada, ocorre uma publicação conservadora. O modo forçado envia todas as dimensões ao SteamCMD, mas a Steam ainda reutiliza chunks idênticos. O código de processo `0` sozinho não basta: a atividade atual do SteamCMD deve confirmar explicitamente `Upload finished ... : OK`, e qualquer falha explícita do Workshop prevalece.

O servidor coordenado permanece online durante o build e todo o upload. Se o conteúdo entregue mudou, o gerenciador aguarda após a confirmação o atraso configurado — no mínimo cinco minutos —, envia `save` e `quit` e aplica a estratégia de reinício. Um no-change verificado ou uma alteração somente de metadados ou preview não reinicia o servidor.

O agendador informa as permissões, valida as dependências, atualiza opcionalmente as origens, constrói, publica e coordena o servidor via RCON quando necessário. Um login supervisionado envia a senha apenas pela entrada padrão do SteamCMD. Uma conta sem Steam Guard continua diretamente. Para uma conta protegida, o SteamCMD envia uma solicitação de aprovação ao Steam Mobile e verifica a resposta automaticamente enquanto a interface mostra a espera ativa. O código atual só é solicitado quando a aprovação expira ou o usuário escolhe essa alternativa; então o PZASM repete o login com o comando documentado `set_steam_guard_code`, também pela entrada padrão. A Steam oferece QR no cliente e nas páginas web, mas o SteamCMD não fornece carga QR nem comando de login por QR documentado; portanto, um QR web separado não pode estabelecer a sessão de publicação. O SteamCMD mantém seu próprio token na pasta portátil; publicações manuais e agendadas usam somente essa sessão. O gerenciador registra apenas o horário da última verificação. Uma sessão expirada solicita nova conexão sem aguardar uma entrada invisível. A interface transmite o progresso ao vivo, aplica um limite de tempo e pode cancelar o processo externo.

O SteamCMD abre uma sessão Steam separada; portanto, a automação deve usar uma conta dedicada de publicação que possua Project Zomboid, e não a conta ativa no cliente desktop. O primeiro login cria o token portátil; verificações posteriores usam `steamcmd verify`, sem senha e sem novo token. O PZASM nunca importa cookies ou arquivos de login do cliente Steam. Publicar pela sessão do cliente exigiria um aplicativo Steamworks autorizado: a publicadora de Project Zomboid deve adicionar o AppID da ferramenta às App Publish Permissions do Workshop para `ISteamUGC`, enquanto OAuth exige um cliente atribuído pela Valve com acesso `write_cloud` limitado ao AppID. Uma ferramenta externa não pode conceder essas permissões a si mesma.

## Aplicativo externo

Um mod executado dentro do jogo não gerencia de forma confiável SteamCMD, horários sem o jogo, arquivos privados ou vários perfis de servidor. Por isso o PZASM usa um aplicativo ASP.NET Core local e um CLI headless com o mesmo núcleo. Apenas o aviso Lua gerado roda no Project Zomboid.

## Segurança e direitos

A [política oficial](https://projectzomboid.com/blog/modding-policy/) é apresentada ao administrador, que continua sendo o único responsável por suas decisões. Status de permissão, provas e confirmação de leitura são apenas informativos: nunca bloqueiam build, publicação ou automação. Casos desconhecidos, sem prova ou recusados continuam claramente visíveis como avisos; provas privadas ficam fora de `Contents` e a descrição pública lista todas as origens.

O Steam pode ocultar um item novo até o aceite do [acordo do Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/).

## Riscos restantes

- mudanças futuras no protocolo ou no Build 42;
- alterações de Mod ID, dependência, mapa ou licença;
- dependências não declaradas e ordem manual de mapas;
- conflitos lógicos impossíveis de detectar estaticamente;
- intervenção ocasional no SteamCMD;
- reinício necessário apenas quando o conteúdo entregue muda, após a confirmação do upload e o atraso configurado.

## Orquestração local e remota

Um perfil representa um INI local ou uma conexão com VPS/servidor dedicado remoto. Um perfil remoto pode usar apenas RCON; SSH e o gerenciamento do INI são opcionais. O estado realiza autenticação RCON real, o console envia comandos administrativos compatíveis e a parada segura usa `save` e depois `quit`.

Os perfis locais têm um modo de execução explícito. Um perfil **Host local** é iniciado pelo menu Host do cliente e usa um processo `zombie.network.GameServer -coop` e `coop-console.txt`. Um perfil **Dedicated local** é iniciado pela ferramenta Steam separada Project Zomboid Dedicated Server (AppID 380870) e usa `server-console.txt`. Os dois modos compartilham intencionalmente os arquivos nativos `Zomboid/Server/<nome>.ini`; o gerenciador armazena o uso escolhido separadamente. Um auxiliar `-coop` só conta como servidor ativo com progresso recente válido ou um marcador de servidor pronto; uma falha posterior o descarta sem criar um conflito falso.

Com systemd, Docker, um painel de hospedagem ou outro supervisor que reinicie o Project Zomboid após `quit`, um perfil somente RCON pode coordenar a publicação: primeiro o upload do Workshop termina e depois o gerenciador envia `save` e `quit`. SSH fica limitado ao gerenciamento INI opcional ou a um comando explícito que inicia apenas o jogo. Comandos do host como `reboot`, `shutdown` e `poweroff` são recusados. O segredo RCON é armazenado localmente para a automação; esse diretório deve ser protegido.

## Oficina de compatibilidade e resolução de conflitos

O editor de pacotes e a visão de implantação do servidor compartilham um analisador estático em cache. Ele lê estruturas Build 42 efetivas (`common` mais a melhor pasta versionada compatível), `require`, `loadAfter`, `loadBefore`, `incompatible`, Mod IDs duplicados, caminhos virtuais Lua/scripts/assets, dependências de mapas e células `.lotheader` sobrepostas. Arquivos diferentes só recebem hash depois da detecção de caminho e tamanho compartilhados; conteúdo idêntico é registrado como informação resolvida.

A oficina propõe uma ordem topológica estável para mods e mapas, mostra as evidências exatas e permite escolher um vencedor prioritário, aceitar uma colisão intencional ou desativar uma fonte. Prioridades manuais viram restrições explícitas de ordem e nunca reescrevem arquivos de terceiros. A auditoria do servidor também relaciona o pacote com `WorkshopItems`, `Mods`, `Map` e falhas recentes do log. A análise estática não consegue provar a compatibilidade de Lua arbitrário, portanto testes dentro do jogo continuam obrigatórios.

Uma violação de ordem causada por uma dependência forte é bloqueante. Componentes fortemente conectados isolam somente os mods do ciclo real, sem incluir todos os mods posteriores. Quando um ciclo é causado apenas por um vencedor manual de colisão que contradiz `require`, `loadAfter` ou `loadBefore`, a oficina pode repará-lo com um clique: remove somente a restrição manual cuja invalidade foi comprovada, reconstrói e valida o grafo e aplica a ordem topológica estável. Se a validação continuar falhando, as restrições removidas são restauradas. Ciclos formados exclusivamente por restrições declaradas pelas fontes continuam sendo bloqueios de resolução manual.

As colisões de arquivos também são classificadas pelo impacto em execução: traduções e mídia passiva de baixo risco, interface do cliente de risco moderado, jogabilidade compartilhada ou scripts de alto risco, e Lua do servidor ou dados de mapas de risco crítico. O diagnóstico separa esses tipos, mostra o primeiro caminho virtual conflitante em cada cabeçalho e pode abrir cada cópia física depois de validar que ela continua dentro de um snapshot de mod gerenciado.

Colisões de texto compatíveis oferecem um editor de diferenças somente leitura. O administrador pode escolher dois mods de origem, trocar os lados, ignorar espaços, alternar entre as visões lado a lado e unificada, pesquisar, manter apenas mudanças com contexto e navegar pelos blocos. O destaque dentro da linha mostra os caracteres exatos alterados. Os caminhos são validados novamente antes da leitura, conteúdo binário é rejeitado, cada arquivo é limitado a 2 MiB e a renderização a 12.000 linhas por lado.

A compatibilidade possui uma aba própria no projeto. O painel principal mostra apenas um resumo compacto do estado e abre essa aba sem repetir a análise. As receitas em lote são deliberadamente restritas: podem desativar mods com ausência verificada da estrutura da versão-alvo, desativar entradas sem fonte ou `mod.info` efetivo disponível e aplicar a ordem calculada de mods e mapas. Cada lote mostra os alvos exatos, preserva os snapshots e deixa colisões ambíguas para revisão explícita.

## Importações com reconhecimento de dependências

Cada importação local ou do Workshop é analisada antes de alterar o projeto. O gerenciador normaliza os Mod IDs `require=` lidos de `mod.info`, compara-os com o pacote atual e lista as dependências ausentes na caixa de confirmação do aplicativo. O administrador pode adicionar o mod selecionado com todas as dependências resolvíveis ou adicionar deliberadamente apenas o mod selecionado.

Dependências locais são associadas pelo Mod ID exato. Para fontes do Workshop, o PZASM também lê a lista oficial **Required Items** do item; recomendações nunca são tratadas como dependências. A correção com um clique aparece tanto no diagnóstico de dependência ausente quanto no cartão do mod afetado. Um item filho baixado só é aceito quando seu `mod.info` efetivo realmente fornece o Mod ID solicitado. Se nenhuma fonte verificada existir, o gerenciador informa o ID não resolvido em vez de adivinhar. As dependências adicionadas são posicionadas antes do mod solicitante e toda a ordem é validada novamente.

## Filtros de descoberta do Workshop

O navegador público do Workshop combina a ordenação do Steam Community com filtragem determinística dos detalhes públicos. A pesquisa pode considerar título e descrição juntos ou separadamente. Várias tags obrigatórias e excluídas são aceitas, com correspondência de todas ou pelo menos uma tag obrigatória. Outros filtros abrangem data de publicação/atualização, SteamID64 do autor, inscrições atuais e acumuladas, favoritos, visualizações, tamanho mínimo/máximo, disponibilidade de imagem/descrição e estado já adicionado no destino.

A profundidade é explícita: uma, três ou cinco páginas de resultados Steam são inspecionadas por lote. IDs candidatos são deduplicados antes da consulta agrupada dos detalhes públicos e as páginas são armazenadas brevemente em cache. Filtros numéricos e de metadados são aplicados após a descoberta para manter comportamento determinístico mesmo quando a página pública ignora um parâmetro URL opcional.
