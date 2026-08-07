# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager (PZASM) est un gestionnaire local pour Project Zomboid et son serveur dédié. Il distribue un ensemble cohérent de mods sous **un seul Workshop ID**, afin que le serveur synchronise le pack au lieu de chaque item source indépendamment.

> État : version fonctionnelle pour Windows et Linux. Le Bundle, les snapshots figés, le catalogue Workshop interne, SteamCMD, la planification autonome ou coordonnée, la fenêtre de connexion, la gestion serveur et le CLI headless sont implémentés. Testez toujours une publication réelle avec un item privé.

## Verdict technique

Un item Workshop Project Zomboid peut contenir plusieurs dossiers sous `mods/`, avec un `mod.info` et un `id=` propres à chacun :

```ini
WorkshopItems=ID_UNIQUE_DU_PACK
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIXE
```

Le serveur et les clients comparent uniquement la version de cet item Workshop global. Les Mod IDs internes servent ensuite au chargement. Les contrôles Lua et checksum habituels restent actifs.

Le mode recommandé est **Bundle** : il conserve les dossiers et Mod IDs originaux. **Fusion stricte** crée un seul Mod ID mais refuse toute collision de fichiers différents.

Consultez l’[étude d’architecture complète](docs/ARCHITECTURE.fr.md).

## Fonctionnalités

- détection du jeu, du serveur dédié, des bibliothèques Steam, de SteamCMD et des mods locaux/Workshop ;
- lecture des structures Build 41/42 et sélection des dossiers de version compatibles ;
- projets indépendants et réouvrables, chacun avec son GUID et son Workshop ID ;
- snapshots privés SHA-256 empêchant une mise à jour Steam locale de modifier silencieusement un build ;
- import par Workshop ID et ajout des dépendances `require=` disponibles ;
- catalogue Workshop interne avec recherche, tris, tags, aperçus, pagination, accès direct par ID et panier de sélection persistant entre les pages avec retrait individuel ;
- même sélecteur visuel pour les packs et les listes `WorkshopItems`/`Mods` des serveurs locaux ou dédiés, avec édition brute conservée ;
- installation portable de SteamCMD en un clic depuis Valve sous Windows et Linux, également disponible avec `pzasm steamcmd install` ;
- téléchargement anonyme des sources Workshop publiques, séparé du compte authentifié utilisé pour publier ;
- Bundle sans réécriture des dossiers, manifests, Lua, scripts, cartes ou assets ;
- Fusion stricte avec déduplication des fichiers identiques et rapport des collisions ;
- description Workshop et manifeste public exhaustifs ;
- suivi des auteurs, licences, autorisations et preuves privées non publiées ;
- statuts et avertissements d’autorisation purement informatifs, sans blocage de la construction, de la publication ou de l’automatisation ; l’administrateur garde le contrôle et la responsabilité ;
- popup de connexion multilingue optionnelle activée par défaut, avec la liste exhaustive, les versions déclarées, les profils PZ et les révisions figées ;
- génération des fichiers Workshop, VDF SteamCMD, configuration serveur et lockfile ;
- création puis mises à jour du même item Workshop ;
- espace projet moderne et adaptatif avec regroupements plus lisibles, cartes de droits repliées par défaut, six langues persistantes et thèmes clair/sombre (clair par défaut) ;
- progression détaillée des imports Workshop : item courant, phase, compteur, pourcentage, résultat de l’analyse et erreur refermable ;
- assistant d’ordre des cartes fondé sur `map.info`, les dépendances `lots=`, les conflits de cellules `.lotheader`, le glisser-déposer et l’édition brute de `Map=` ;
- éditeur serveur guidé pour l’identité, l’accès, RCON, la session, les sauvegardes et le contenu, complété par l’éditeur INI brut avec préservation de l’encodage ;
- statut RCON authentifié, console de commandes, arrêt propre `save`/`quit` et redémarrage coordonné, y compris avec un profil distant sans SSH ;
- progression détaillée et annulable pour la publication, l’authentification SteamCMD et les mises à jour de mods, avec sortie SteamCMD en direct et délai maximal ;
- UI locale et CLI headless sous Windows et Linux ;
- daemon `automation run` avec verrous entre processus.

### Commandes du projet et mises à jour

Construire, Mettre à jour les mods et Publier sont présentées comme les commandes principales du projet. Les actions sensibles utilisent toujours une fenêtre de confirmation intégrée à l’interface, jamais un dialogue natif du navigateur. L’auteur et le détenteur des droits sont préremplis depuis le `mod.info` de chaque source lorsqu’ils sont disponibles, tout en restant modifiables. Chaque mod peut être exclu de la mise à jour globale et actualisé individuellement ; son snapshot reste figé tant que sa mise à jour individuelle n’est pas explicitement demandée.

## Démarrage

Depuis les sources, installez le [SDK .NET 9](https://dotnet.microsoft.com/download/dotnet/9.0). Les artefacts autonomes de la CI n’exigent pas le runtime .NET.

Windows :

```powershell
Start-PZASM.cmd
```

Linux :

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

L’UI écoute localement sur `http://localhost:5160`. Utilisez `--data-root <dossier>` pour partager explicitement les données entre l’UI et le CLI.
SteamCMD s’installe depuis le tableau de bord ou l’onglet Distribution. Les sources publiques Project Zomboid sont téléchargées anonymement par défaut ; seul le compte éditeur est requis pour publier.

SteamCMD télécharge un Workshop ID connu mais ne fournit pas de recherche complète. Le catalogue interne énumère les résultats publics Steam Community, récupère leurs métadonnées publiques, puis transmet uniquement la sélection à SteamCMD. Une publication planifiée ne nécessite aucun serveur local ; la coordination RCON reste facultative.

## Workflow recommandé

1. Créez un projet en mode **Bundle**.
2. Ajoutez les mods détectés ou importez un Workshop ID.
3. Renseignez l’auteur et les autorisations de chaque source.
4. Vérifiez l’ordre des mods et des cartes.
5. Construisez et examinez `pack.lock.json` et `server-config.txt`.
6. Installez SteamCMD en un clic, configurez le compte éditeur, utilisez **Connecter / renouveler la session**, puis publiez d’abord en privé.
7. Testez sur un serveur de staging avant la production.

## CLI headless

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd login --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Serveur principal"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Chaque projet représente un pack global séparé. Rien ne se met à jour automatiquement sans activation explicite. Des unités systemd de référence se trouvent dans `deploy/systemd/`.

## SteamCMD et serveurs distants

Le mot de passe Steam et le code Steam Guard sont transmis à SteamCMD par son entrée standard uniquement pendant la demande ; ils ne sont ni placés dans la ligne de commande, ni enregistrés par PZASM. SteamCMD conserve son propre jeton dans son dossier portable pour la planification. Si la session expire ou qu’un secret manque, la publication s’arrête immédiatement avec une explication au lieu d’attendre silencieusement. L’UI affiche la sortie en direct et permet d’annuler le processus externe.

Un profil distant peut fonctionner avec RCON uniquement : statut authentifié, console, `save`, `quit` et coordination sont disponibles sans SSH. Si systemd, Docker, le panel ou l’hébergeur relance Project Zomboid après `quit`, PZASM publie d’abord puis demande le redémarrage propre par RCON. SSH reste facultatif pour lire/modifier l’INI ou lancer explicitement le processus du jeu. PZASM ne redémarre jamais le VPS ou le serveur dédié lui-même.

## Droits et responsabilité

PZASM ne donne aucun droit sur les mods inclus. La [politique officielle Project Zomboid](https://projectzomboid.com/blog/modding-policy/) exige les autorisations appropriées et une liste complète pour les packs publics ou non listés. Steam exige aussi l’acceptation de son [accord Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/).

Le créateur et l’éditeur du pack restent seuls responsables des autorisations, licences, crédits et contenus tiers. LemonCorp et les contributeurs de PZASM ne sont pas responsables des packs construits ou publiés par les utilisateurs.

## Développement

Le dépôt fournit un `Justfile` multiplateforme. Installez [just](https://github.com/casey/just), puis utilisez :

```text
just                 # afficher toutes les recettes
just check           # vérifier le formatage, compiler en Release et tester
just build           # compiler toute la solution
just test            # exécuter tous les tests
just run-ui           # démarrer l’UI et ouvrir le navigateur
just run-cli help     # exécuter une commande CLI
just automation      # démarrer le planificateur headless
just publish          # publier pour le système courant
just publish-all      # publier win-x64 et linux-x64
```

Les variables d’environnement `CONFIGURATION` et `PUBLISH_DIR` permettent de modifier la configuration `Release` et le dossier `publish` par défaut. Les recettes acceptent aussi des arguments supplémentaires.

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

N’exposez pas le port PZASM à Internet : l’interface est prévue pour une administration locale et n’implémente pas d’authentification réseau.
