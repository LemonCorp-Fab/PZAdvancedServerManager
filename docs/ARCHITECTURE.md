# Architecture et étude de faisabilité

## Conclusion

Le bon modèle n'est pas de concaténer aveuglément tous les répertoires `media`. Project Zomboid sait déjà charger plusieurs mods depuis un même item Workshop. PZASM exploite cette distinction :

```text
un PublishedFileId Workshop
└── mods/
    ├── ModA/          → mod.info : id=ModA
    ├── ModB/          → mod.info : id=ModB
    └── PZASM_Notice/  → mod.info : id=PZASM_Notice_SUFFIXE
```

Le jeu voit bien plusieurs **Mod IDs**, car ils sont nécessaires à son chargeur, mais il ne voit qu'un **Workshop ID à synchroniser**. Cela atteint l'objectif de stabilité sans les risques d'une fusion de fichiers.

## Ce que le client Project Zomboid contrôle

L'inspection de la version locale 42.20.2 montre deux phases distinctes dans l'état de connexion client :

1. la liste Workshop reçue du serveur contient un identifiant d'item et son horodatage ; le client compare l'horodatage installé/publié et déclenche le statut de version différente pour cet item ;
2. la liste `Mods=` est ensuite chargée par Mod ID ; l'association Mod ID → Workshop ID sert notamment à expliquer un mod absent, mais aucun second horodatage Workshop n'est attaché à chaque sous-mod.

Ensuite, le protocole multijoueur continue avec les vérifications normales, notamment le checksum Lua lorsque `DoLuaChecksum=true`. Le Bundle supprime donc le problème de décalage entre N items Workshop, sans désactiver les protections de cohérence du jeu.

Cette observation est spécifique à la version analysée et doit être couverte par des tests de compatibilité lors des futures mises à jour majeures de Project Zomboid.

## Structure rencontrée sur disque

Les items téléchargés pour l'App ID `108600` ont généralement cette forme :

```text
steamapps/workshop/content/108600/<WorkshopId>/
├── mods/
│   └── <DossierLogique>/
│       ├── mod.info                  # manifeste historique / fallback
│       ├── media/                    # contenu historique
│       ├── common/
│       │   ├── mod.info
│       │   └── media/
│       ├── 42.0/
│       │   ├── mod.info
│       │   └── media/
│       └── 42.13/
│           ├── mod.info
│           └── media/
└── workshop.txt ou autres métadonnées locales selon la source
```

Un item Workshop peut déjà contenir plusieurs dossiers logiques. `mod.info` fournit notamment `name`, `id`, `author`, `description`, `poster`, `require`, contraintes de version et métadonnées libres. Le répertoire `media` peut contenir :

- `lua/client`, `lua/server`, `lua/shared` ;
- `scripts` et définitions d'items/véhicules/recettes ;
- `maps`, lots, cellules, zones de spawn ;
- textures, UI, modèles, animations, sons, radios, traductions et autres assets.

Pour Build 42, PZASM conserve tout le dossier en mode Bundle. Pour les analyses et la Fusion stricte, il compose le contenu effectif dans l'ordre `media` historique, `common/media`, puis variante numérique compatible la plus élevée.

## Pourquoi la fusion complète est risquée

Deux mods indépendants peuvent employer la même destination relative sans parler du même objet :

- `media/lua/client/...` : remplacement de module, `require`, événements enregistrés deux fois, globals identiques ;
- `media/scripts/...` : IDs d'items, recettes, véhicules ou distributions identiques ;
- textures/modèles : même nom de ressource mais contenu différent ;
- cartes : mêmes cellules, dossiers, lots ou zones ;
- traductions/UI : mêmes clés ;
- Java : classes/JAR, chargement natif et compatibilité de version.

Réécrire seulement les chemins n'est pas suffisant : les références peuvent être dans Lua, les scripts, les modèles, les cartes ou du bytecode. PZASM applique donc une règle déterministe : fichier identique = déduplication ; variante plus récente du même mod = remplacement de sa partie `common` ; collision différente entre deux mods = erreur de build.

## Les deux modes

### Bundle — recommandé

- un Workshop ID ;
- N Mod IDs originaux, plus la notice optionnelle ;
- dossiers de chaque mod copiés sans modification ;
- dépendances `require=` ajoutées si elles sont installées ;
- description et lockfile exhaustifs ;
- compatibilité maximale avec les appels comme `getActivatedMods()`, `getModInfoByID()` et `getModFileReader()`.

### Fusion stricte — avancé

- un Workshop ID et un Mod ID généré `PZASM_Pack_<suffixe>` ;
- fusion du contenu `media` effectif ;
- aucune décision silencieuse en cas de collision ;
- utile seulement pour un ensemble maîtrisé et testé ;
- certains mods qui recherchent leur propre Mod ID ou leur racine ne peuvent pas fonctionner sans patch explicite.

## Projet durable et mises à jour

Chaque projet possède un GUID immuable. Le suffixe de ses Mod IDs PZASM est dérivé de ce GUID. Le fichier de projet conserve aussi le `publishedfileid` Steam :

- valeur `0` : SteamCMD crée un item ;
- après succès : SteamCMD réécrit le VDF avec le nouvel ID, que PZASM mémorise ;
- publications suivantes : le même ID est utilisé et l'item est mis à jour.

Le lockfile contient la liste des sources et un SHA-256 de chaque fichier livré. Cela permet de savoir exactement ce qui constituait un build donné, même si les sources Workshop ont changé ensuite.

## Publication et planification

Steam documente que `workshop_build_item` crée un item lorsque `publishedfileid=0`, puis met ce champ à jour pour permettre les publications suivantes sur le même item. Voir le [guide Steamworks Workshop](https://partner.steamgames.com/doc/features/workshop/implementation).

Le planificateur PZASM :

1. vérifie qu'un horaire est dû ;
2. refuse de publier si une autorisation, une dépendance ou un fichier est invalide ;
3. peut demander à SteamCMD d'actualiser chaque item source ;
4. repointe les sources vers le cache SteamCMD correspondant au même Mod ID ;
5. reconstruit le pack dans un répertoire temporaire ;
6. publie le VDF sur le même Workshop ID ;
7. conserve l'état et le résultat dans le projet.

Aucun mot de passe ni code Steam Guard n'est persisté. L'utilisateur doit préparer la session SteamCMD du compte. Une automatisation de production devrait utiliser un compte limité et un serveur de staging.

## Fenêtre injectée

La notice est un petit mod Lua client séparé en mode Bundle, et un fichier client intégré au Mod ID du pack en mode Fusion. Sur `Events.OnConnected`, elle ouvre une fenêtre défilante contenant :

- le nom PZ Advanced Server Manager ;
- le titre et la description choisis ;
- un avertissement clair sur les droits ;
- chaque mod, son auteur, son Mod ID et son Workshop ID source.

L'injection est activée par défaut mais peut être désactivée au niveau du projet. Elle ne télécharge rien et ne contacte aucun service externe.

## Pourquoi un exécutable/service externe est nécessaire

Un mod PZ s'exécute dans le contexte et le cycle de vie du jeu. Il n'est pas une base fiable pour :

- parcourir toutes les bibliothèques Steam avant le lancement ;
- copier et hacher des dizaines de milliers de fichiers ;
- conserver des projets et justificatifs privés ;
- lancer SteamCMD, gérer Steam Guard ou publier un item ;
- planifier une mise à jour quand aucun jeu n'est lancé ;
- éditer et sauvegarder proprement plusieurs profils serveur.

PZASM est donc une application ASP.NET Core locale avec un worker d'arrière-plan, complétée par un CLI headless qui utilise exactement le même cœur et le même format de projet. Les deux sont publiés pour Windows x64 et Linux x64. Le seul composant exécuté par Project Zomboid est le mod de notice généré.

## Modèle multi-projets

Un projet correspond à un mod global/pack Workshop indépendant :

- GUID et suffixe PZASM propres ;
- un `publishedfileid` propre, créé à la première publication ;
- sources, versions, droits, cartes et serveur associé propres ;
- mises à jour suivantes envoyées exclusivement sur ce même Workshop ID.

Créer un autre projet crée donc un autre pack sans écraser ni coupler le premier. L'UI et le CLI affichent et rouvrent le même catalogue de projets.

## Windows, Linux et headless

Le cœur .NET détecte les bibliothèques Steam classiques des deux systèmes, `steamcmd.exe` ou `steamcmd.sh`, `StartServer64.bat` ou `start-server.sh`, et conserve ses données dans le répertoire applicatif local de l'OS. L'UI web locale ne dépend pas d'un framework graphique natif et fonctionne donc de manière identique sous Linux.

Le CLI couvre l'inventaire, les projets, les droits, la validation, le build, la publication volontaire avec `--yes`, ainsi que le statut/démarrage/arrêt/application serveur. Il convient aux serveurs administrés par SSH, aux conteneurs persistants et aux services systemd.

## Sécurité, droits et publication

La [politique officielle Project Zomboid](https://projectzomboid.com/blog/modding-policy/) exige l'autorisation de chaque auteur pour les packs publics et pour les packs serveur non listés. Une copie personnelle n'est exemptée que si elle n'est ni publiée ni rendue téléchargeable. La liste complète des sources doit être visible.

Conséquences dans PZASM :

- l'utilisateur doit accepter l'avertissement global ;
- chaque source a un statut et une preuve ;
- un statut inconnu autorise un build local mais bloque la publication ;
- un refus bloque le build ;
- les preuves privées restent hors de `Contents` ;
- la description publique est générée, exhaustive et ne peut pas omettre discrètement une source ;
- LemonCorp ne garantit pas les déclarations saisies par l'utilisateur et ne saurait être responsable de leur exactitude.

Steam peut maintenir un nouvel item caché tant que le contributeur n'a pas accepté l'[accord légal Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/).

## Risques opérationnels restants

- Une mise à jour PZ peut modifier le protocole ou la structure Build 42.
- Un auteur peut changer un Mod ID, une dépendance ou sa licence.
- Les cartes peuvent nécessiter un ordre manuel particulier.
- Un mod peut dépendre d'un autre sans déclarer `require=`.
- Deux sources peuvent déclarer le même Mod ID.
- Les scripts serveur et client restent soumis à `DoLuaChecksum`.
- SteamCMD est prévu par Valve surtout comme outil technique/test et peut réclamer une intervention de compte.
- Un serveur doit être redémarré proprement après publication pour charger le nouveau pack ; PZASM ne force pas actuellement l'arrêt d'un processus serveur sans orchestration explicite.
