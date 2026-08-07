# Architecture et étude de faisabilité

[English](ARCHITECTURE.md) · [Français](ARCHITECTURE.fr.md) · [Español](ARCHITECTURE.es.md) · [Deutsch](ARCHITECTURE.de.md) · [Português (Brasil)](ARCHITECTURE.pt-BR.md) · [简体中文](ARCHITECTURE.zh-CN.md)

## Conclusion

Project Zomboid sait charger plusieurs mods logiques depuis un même item Workshop. Le Bundle PZASM utilise cette distinction :

```text
un PublishedFileId Workshop
└── mods/
    ├── ModA/          → mod.info : id=ModA
    ├── ModB/          → mod.info : id=ModB
    └── PZASM_Notice/  → mod.info : id=PZASM_Notice_SUFFIXE
```

Le jeu voit plusieurs **Mod IDs**, nécessaires à son chargeur, mais un seul **Workshop ID à synchroniser**. La configuration serveur contient donc un Workshop ID global et la liste des Mod IDs internes.

## Vérification client/serveur

L’inspection locale de la version 42.20.2 montre deux phases :

1. le client reçoit les Workshop IDs et horodatages du serveur puis vérifie chaque item ;
2. il charge ensuite `Mods=` par Mod ID, sans horodatage Workshop séparé pour chaque sous-mod du même item.

Les contrôles multijoueur habituels restent actifs, notamment `DoLuaChecksum`. Le Bundle supprime le décalage entre plusieurs items Workshop indépendants sans désactiver les protections d’intégrité.

Cette observation doit être retestée lors des mises à jour majeures de Project Zomboid.

## Structure des mods

```text
steamapps/workshop/content/108600/<WorkshopId>/
└── mods/<DossierLogique>/
    ├── mod.info
    ├── media/
    ├── common/mod.info + media/
    └── 42.x/mod.info + media/
```

`mod.info` décrit notamment le nom, le Mod ID, l’auteur, les dépendances et les contraintes de version. `media` peut contenir Lua client/serveur/partagé, scripts, cartes, textures, modèles, animations, sons, radios, traductions et interface.

Le Bundle conserve les dossiers complets. Pour l’analyse et la Fusion stricte, le contenu effectif est composé dans l’ordre suivant : `media` historique, `common/media`, puis variante numérique compatible la plus élevée.

## Bundle et Fusion stricte

### Bundle — recommandé

- un Workshop ID ;
- les Mod IDs originaux et la notice optionnelle ;
- aucune réécriture sémantique des sources ;
- compatibilité maximale avec les dépendances et API de mods.

### Fusion stricte — avancé

- un Workshop ID et un Mod ID `PZASM_Pack_<suffixe>` ;
- fusion du contenu effectif ;
- fichiers identiques dédupliqués ;
- toute collision de contenus différents bloque le build.

Une fusion universelle est impossible sans risques : les références peuvent se trouver dans Lua, les scripts, les cartes, les modèles ou le bytecode. Deux mods peuvent également réutiliser les mêmes globals, IDs de scripts, cellules de cartes, ressources ou clés de traduction.

## Projets et versions figées

Chaque projet possède un GUID immuable et son propre `publishedfileid`. La valeur `0` demande à SteamCMD de créer un item ; après publication, l’ID réécrit dans le VDF est conservé pour les mises à jour suivantes.

Lors de l’ajout d’une source, PZASM crée un snapshot privé et calcule son SHA-256. Les builds utilisent ce snapshot, jamais le cache Steam mutable. Une actualisation explicite télécharge, valide puis remplace atomiquement le snapshot. `pack.lock.json` décrit exactement le contenu livré.

## Publication et orchestration

Le [guide Steamworks Workshop](https://partner.steamgames.com/doc/features/workshop/implementation) documente la création avec `publishedfileid=0` puis la mise à jour du même item.

Le planificateur valide les droits et dépendances, actualise éventuellement les sources, remplace les snapshots, construit, publie et coordonne éventuellement le serveur par RCON. Une connexion supervisée transmet le mot de passe à SteamCMD par son entrée standard, sans l’enregistrer. Un compte sans Steam Guard continue directement. Pour un compte protégé, SteamCMD envoie une demande d’approbation dans Steam Mobile et la vérifie automatiquement pendant que l’UI affiche l’attente active. Le code actuel n’est demandé que si l’approbation expire ou si l’utilisateur choisit ce recours ; PZASM relance alors la connexion avec la commande documentée `set_steam_guard_code`, toujours par l’entrée standard. Steam propose le QR dans son client et sur ses pages web, mais SteamCMD n’expose ni charge utile QR ni commande de connexion QR documentée : un QR web séparé ne peut donc pas établir la session de publication. SteamCMD conserve ensuite son propre jeton dans son dossier portable ; les publications manuelles et planifiées utilisent uniquement cette session. Le manager ne mémorise que l’heure de la dernière vérification. Une session expirée demande une reconnexion au lieu d’attendre sur une invite invisible. L’UI diffuse la progression en direct, applique un délai maximal et peut annuler le processus externe.

## Application externe nécessaire

Un mod Project Zomboid ne peut pas gérer de manière fiable SteamCMD, les fichiers avant le lancement, les preuves privées, les horaires hors jeu ou plusieurs profils serveur. PZASM utilise donc une application ASP.NET Core locale et un CLI headless partageant le même cœur. Seule la notice Lua générée s’exécute dans le jeu.

## Sécurité et droits

La [politique officielle Project Zomboid](https://projectzomboid.com/blog/modding-policy/) est présentée à l’administrateur, qui reste seul responsable de ses décisions. Les statuts d’autorisation, preuves et accusés de lecture sont uniquement documentaires : ils ne bloquent jamais la construction, la publication ou l’automatisation. Les situations inconnues, sans preuve ou refusées restent clairement signalées ; les preuves privées restent hors de `Contents` et la description publique liste toutes les sources.

Steam peut conserver un nouvel item masqué tant que l’[accord Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/) n’a pas été accepté.

## Risques restants

- évolution du protocole ou de la structure Build 42 ;
- changement de Mod ID, dépendance, carte ou licence par un auteur ;
- dépendances non déclarées et ordre manuel des cartes ;
- conflits logiques impossibles à détecter statiquement ;
- intervention SteamCMD parfois nécessaire ;
- redémarrage serveur obligatoire après publication.

## Orchestration locale et distante

Un profil représente soit un fichier INI local, soit une connexion vers un VPS ou serveur dédié distant. Un profil distant peut utiliser uniquement RCON ; SSH et la gestion de l’INI sont facultatifs. Le statut effectue une authentification RCON réelle, la console accepte les commandes d’administration prises en charge et l’arrêt propre utilise `save`, puis `quit`.

Avec un superviseur systemd, Docker, un panel ou un hébergeur qui relance le jeu après `quit`, un profil RCON-only peut coordonner la publication : l’envoi Workshop se termine d’abord, puis le manager transmet `save` et `quit`. SSH sert uniquement à la gestion INI facultative ou à une commande de démarrage explicite du jeu. Les commandes `reboot`, `shutdown` et `poweroff` de l’hôte sont refusées. Le secret RCON est conservé dans les données locales du manager pour l’automatisation : ce dossier doit être protégé.
