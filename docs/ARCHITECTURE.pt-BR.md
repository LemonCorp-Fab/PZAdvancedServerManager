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

O agendador informa os registros de permissão e valida as dependências, atualiza opcionalmente as origens, constrói em uma pasta temporária, executa `save` e `quit` via RCON, publica e reinicia o servidor se ele estava ativo. Senhas da Steam e códigos Steam Guard não são armazenados.

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
- reinício obrigatório do servidor após a publicação.

## Orquestração local e remota

Um perfil representa um INI local ou uma conexão com VPS/servidor dedicado remoto. O estado não usa apenas uma sondagem de porta TCP: o PZASM autentica via RCON e só considera o Project Zomboid ativo quando a senha é aceita. A parada segura sempre envia `save` e depois `quit` por RCON.

O SSH serve apenas para testar a conexão, transferir o INI remoto e executar o comando configurado que inicia o processo ou serviço do Project Zomboid. O acesso não interativo usa chave privada ou agente SSH. Comandos do host como `reboot`, `shutdown` e `poweroff` são recusados. Uma publicação coordenada para e inicia somente o jogo; o sistema operacional do VPS/dedicado continua ativo. O segredo RCON é armazenado nos dados locais do gerenciador para a automação; esse diretório deve ser protegido.
