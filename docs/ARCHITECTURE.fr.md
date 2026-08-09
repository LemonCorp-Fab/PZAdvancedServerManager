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

La publication est incrémentale à deux niveaux. PZASM calcule séparément les empreintes du contenu livré, des métadonnées et de la preview, puis omet du VDF les dimensions inchangées. SteamCMD et Steam comparent ensuite le manifeste soumis au précédent et ne transmettent que les chunks absents. PZASM ne retélécharge jamais le package après l’upload.

Un résultat « aucun changement » exige les trois empreintes locales ainsi qu’une nouvelle lecture par l’API publique des handles de contenu et de preview, de la taille, de l’heure de mise à jour, du titre, de la description et de la visibilité distants. Tout élément invérifiable ou périmé déclenche une publication conservatrice. Le mode forcé envoie toutes les dimensions à SteamCMD, tout en laissant Steam réutiliser les chunks identiques. Un code processus `0` ne suffit pas : l’activité SteamCMD courante doit confirmer explicitement `Upload finished ... : OK`, et toute erreur Workshop explicite l’emporte.

Le serveur coordonné reste actif pendant la construction et tout l’upload. Si le contenu livré a changé, le manager attend après la confirmation le délai configuré — cinq minutes au minimum — puis envoie `save` et `quit` et applique la stratégie de redémarrage. Un no-change vérifié ou une modification limitée aux métadonnées ou à la preview ne redémarre pas le serveur.

Le planificateur valide les droits et dépendances, actualise éventuellement les sources, remplace les snapshots, construit, publie et coordonne éventuellement le serveur par RCON. Une connexion supervisée transmet le mot de passe à SteamCMD par son entrée standard, sans l’enregistrer. Un compte sans Steam Guard continue directement. Pour un compte protégé, SteamCMD envoie une demande d’approbation dans Steam Mobile et la vérifie automatiquement pendant que l’UI affiche l’attente active. Le code actuel n’est demandé que si l’approbation expire ou si l’utilisateur choisit ce recours ; PZASM relance alors la connexion avec la commande documentée `set_steam_guard_code`, toujours par l’entrée standard. Steam propose le QR dans son client et sur ses pages web, mais SteamCMD n’expose ni charge utile QR ni commande de connexion QR documentée : un QR web séparé ne peut donc pas établir la session de publication. SteamCMD conserve ensuite son propre jeton dans son dossier portable ; les publications manuelles et planifiées utilisent uniquement cette session. Le manager ne mémorise que l’heure de la dernière vérification. Une session expirée demande une reconnexion au lieu d’attendre sur une invite invisible. L’UI diffuse la progression en direct, applique un délai maximal et peut annuler le processus externe.

SteamCMD ouvre une session Steam distincte : l’automatisation doit donc utiliser un compte de publication dédié qui possède Project Zomboid, et non le compte actif dans le client Steam de bureau. La première connexion crée le jeton portable ; les contrôles suivants utilisent `steamcmd verify`, sans mot de passe ni nouveau jeton. PZASM n’importe jamais les cookies ou fichiers de connexion du client Steam. Une publication via la session du client nécessiterait une application Steamworks autorisée : l’éditeur de Project Zomboid doit ajouter l’AppID de l’outil aux App Publish Permissions du Workshop pour `ISteamUGC`, tandis qu’OAuth exige un client attribué par Valve avec l’accès `write_cloud` limité à l’AppID. Un outil externe ne peut s’accorder lui-même aucun de ces droits.

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
- redémarrage requis uniquement lorsque le contenu livré change, après confirmation de l’upload et expiration du délai configuré.

## Orchestration locale et distante

Un profil représente soit un fichier INI local, soit une connexion vers un VPS ou serveur dédié distant. Un profil distant peut utiliser uniquement RCON ; SSH et la gestion de l’INI sont facultatifs. Le statut effectue une authentification RCON réelle, la console accepte les commandes d’administration prises en charge et l’arrêt propre utilise `save`, puis `quit`.

Les profils locaux possèdent un mode d’exécution explicite. Un profil **Host local** est lancé depuis le menu Host du client et utilise un processus `zombie.network.GameServer -coop` ainsi que `coop-console.txt`. Un profil **Dedicated local** est lancé par l’outil Steam Project Zomboid Dedicated Server séparé (AppID 380870) et utilise `server-console.txt`. Les deux modes référencent volontairement les mêmes fichiers natifs `Zomboid/Server/<nom>.ini` ; le manager conserve le choix d’usage séparément. Un auxiliaire `-coop` n’est actif qu’après une progression récente valide ou le marqueur serveur prêt ; un échec de démarrage ultérieur l’exclut sans créer de faux conflit.

Avec un superviseur systemd, Docker, un panel ou un hébergeur qui relance le jeu après `quit`, un profil RCON-only peut coordonner la publication : l’envoi Workshop se termine d’abord, puis le manager transmet `save` et `quit`. SSH sert uniquement à la gestion INI facultative ou à une commande de démarrage explicite du jeu. Les commandes `reboot`, `shutdown` et `poweroff` de l’hôte sont refusées. Le secret RCON est conservé dans les données locales du manager pour l’automatisation : ce dossier doit être protégé.

## Atelier de compatibilité et de résolution des conflits

L’éditeur de pack et la vue de déploiement serveur partagent un analyseur statique mis en cache. Il lit les structures Build 42 effectives (`common` plus le meilleur dossier versionné compatible), `require`, `loadAfter`, `loadBefore`, `incompatible`, les Mod IDs dupliqués, les chemins virtuels Lua/scripts/assets, les dépendances de cartes et les cellules `.lotheader` superposées. Les fichiers différents ne sont hachés qu’après détection d’un chemin et d’une taille partagés ; un contenu identique est marqué comme information déjà résolue.

L’atelier propose un ordre topologique stable des mods et un ordre des cartes, expose les preuves exactes et permet de choisir un gagnant prioritaire, documenter une collision volontaire ou désactiver une source. Un choix manuel devient une contrainte d’ordre explicite et ne réécrit jamais les fichiers tiers. L’audit serveur rapproche aussi le pack de `WorkshopItems`, `Mods`, `Map` et des erreurs récentes du journal. Une analyse statique ne peut pas garantir la compatibilité de Lua arbitraire : un test en jeu reste obligatoire.

Une violation d’ordre issue d’une dépendance forte est bloquante. Les composantes fortement connexes isolent uniquement les mods du cycle réel, sans inclure les mods simplement situés en aval. Lorsqu’un cycle provient seulement d’un gagnant de collision manuel qui contredit `require`, `loadAfter` ou `loadBefore`, l’atelier peut le réparer en un clic : il retire uniquement la contrainte manuelle dont l’invalidité est prouvée, reconstruit et valide le graphe, puis applique l’ordre topologique stable. Si la validation échoue encore, les contraintes retirées sont restaurées. Un cycle composé uniquement de contraintes déclarées par les sources reste un blocage à résoudre manuellement.

Les collisions de fichiers sont aussi classées par impact à l'exécution : traductions et médias passifs à faible risque, interface client à risque modéré, gameplay partagé ou scripts à risque élevé, Lua serveur et données de carte à risque critique. Le diagnostic sépare ces types, affiche le premier chemin virtuel conflictuel dans chaque en-tête et peut ouvrir chaque copie physique après avoir vérifié qu'elle reste dans un snapshot de mod géré.

Les collisions de texte compatibles donnent accès à un éditeur de diff en lecture seule. L'administrateur peut choisir deux mods sources, inverser les côtés, ignorer les espaces, passer de la vue côte à côte à la vue unifiée, rechercher, conserver uniquement les changements avec leur contexte et naviguer entre les blocs. Le surlignage intra-ligne montre les caractères exacts modifiés. Les chemins sont revérifiés avant lecture, le contenu binaire est refusé, chaque fichier est limité à 2 Mio et le rendu à 12 000 lignes par côté.

La compatibilité possède son propre onglet de projet. Le tableau de bord n’affiche qu’un résumé compact de l’état et ouvre cet onglet sans relancer l’analyse. Les recettes par lot restent volontairement strictes : elles peuvent désactiver les mods dont l’absence de structure pour la version cible est vérifiée, désactiver les entrées dont la source ou le `mod.info` effectif est indisponible, puis appliquer l’ordre calculé des mods et cartes. Chaque lot affiche ses cibles exactes, conserve les snapshots et laisse les collisions ambiguës à l’arbitrage explicite.

## Imports conscients des dépendances

Chaque import local ou Workshop est analysé avant toute modification du projet. Le manager normalise les Mod IDs `require=` lus dans `mod.info`, les compare au pack actuel et liste les dépendances manquantes dans la boîte de confirmation de l’application. L’administrateur peut ajouter le mod sélectionné avec toutes les dépendances résolubles ou choisir délibérément de n’ajouter que ce mod.

Les dépendances locales sont associées par Mod ID exact. Pour une source Workshop, PZASM lit aussi la liste officielle **Required Items** de l’item ; les recommandations ne sont jamais considérées comme des dépendances. Une correction en un clic est disponible dans le diagnostic de dépendance manquante et sur la carte du mod concerné. Un item Workshop téléchargé n’est accepté que si son `mod.info` effectif fournit réellement le Mod ID demandé. Sans source vérifiée, le manager signale l’ID non résolu au lieu de deviner. Les dépendances ajoutées sont placées avant le mod demandeur, puis l’ordre complet est de nouveau validé.
