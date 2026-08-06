# PZ Advanced Server Manager

PZ Advanced Server Manager (PZASM) est un gestionnaire local pour Project Zomboid et Project Zomboid Dedicated Server. Son objectif principal est de distribuer un ensemble cohérent de mods sous **un seul Workshop ID**, afin que le contrôle de version Workshop du serveur porte sur le pack et non sur chacun des items sources.

> Statut : première version fonctionnelle Windows et Linux. Le mode Bundle, la persistance multi-projets, l'analyse locale, la construction, la publication SteamCMD, la planification coordonnée, la fenêtre de connexion, l'édition des configurations serveur et le CLI headless sont implémentés. Une publication réelle doit toujours être testée sur un item privé avant utilisation en production.

## Verdict technique

Oui, le concept fonctionne sans fusionner les dossiers `media` : un item Workshop Project Zomboid peut contenir plusieurs dossiers sous `mods/`, chacun avec son propre `mod.info` et son propre `id=`. La configuration serveur référence alors :

```ini
WorkshopItems=ID_UNIQUE_DU_PACK
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIXE
```

Le serveur et les clients comparent la version de l'unique item Workshop. Les Mod IDs internes servent ensuite au chargement. Les contrôles Lua/checksum normaux de Project Zomboid restent actifs : un pack incohérent ou modifié localement peut donc encore être refusé, ce qui est souhaitable.

Le mode recommandé s'appelle **Bundle**. Le mode **Fusion stricte** produit réellement un seul Mod ID, mais refuse toute collision de fichiers non identiques au lieu de choisir silencieusement un gagnant.

L'analyse complète est dans [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Fonctionnalités présentes

- détection des bibliothèques Steam, du jeu, du serveur dédié, de SteamCMD, des mods locaux et des items Workshop `108600` ;
- lecture des structures Build 41/42, dont `common`, `42`, `42.13`, etc., avec sélection de la variante compatible la plus élevée ;
- projets `.pzasm.json` réouvrables : GUID/suffixe stable, Workshop ID publié, sources, versions, ordre, cartes, automatisation et autorisations ;
- ajout automatique des dépendances `require=` disponibles localement et blocage si une dépendance manque ;
- Bundle conservant les dossiers, `mod.info`, Mod IDs, Lua, scripts, cartes et assets d'origine ;
- Fusion stricte avec déduplication des fichiers identiques et rapport des collisions incompatibles ;
- description Workshop exhaustive avec auteur, Mod ID, lien d'origine, Workshop ID et statut des droits ;
- justificatifs publics et pièces privées locales — les pièces privées ne sont jamais placées dans `Contents` ;
- mod de notice injecté par défaut : popup lors de la connexion, description du pack, avertissement et liste exhaustive ;
- génération de `workshop.txt`, `steamcmd-item.vdf`, `server-config.txt`, aperçu PNG, manifeste public dans le pack et `pack.lock.json` avec SHA-256 ;
- création et mise à jour du même item par SteamCMD ; le `publishedfileid` réécrit est mémorisé dans le projet ;
- planification quotidienne optionnelle : actualisation SteamCMD, reconstruction et publication ;
- éditeur complet des fichiers `Zomboid/Server/*.ini`, préservation de l'encodage et sauvegarde horodatée ;
- application d'un pack publié à un serveur en remplaçant uniquement `WorkshopItems`, `Mods` et `Map`.
- orchestration sûre : statut RCON, `save`, `quit`, démarrage Windows/Linux et redémarrage autour d'une publication planifiée ;
- CLI Windows/Linux pour les machines sans bureau ou administrées par SSH.

## Démarrage

Prérequis depuis les sources : Windows ou Linux et le SDK [.NET 9](https://dotnet.microsoft.com/download/dotnet/9.0). Les artefacts autonomes de la CI n'exigent pas le runtime .NET. SteamCMD n'est requis que pour actualiser/publier.

Double-cliquer sur `Start-PZASM.cmd` (le navigateur s'ouvre quand le service est prêt), ou :

```powershell
dotnet run --project src/PZAdvancedServerManager.App -- --open-browser
```

Sous Linux :

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

L'interface écoute uniquement sur la machine locale par défaut, à `http://localhost:5160`. Les données utilisateur sont conservées dans :

```text
%LOCALAPPDATA%\LemonCorp\PZAdvancedServerManager
```

Les sources Steam et les configurations PZ ne sont jamais modifiées pendant un build. L'application copie les sources vers son dossier de build.

## Workflow conseillé

1. Créer un projet et conserver le mode **Bundle**.
2. Décrire clairement le pack et laisser la fenêtre de connexion activée.
3. Ajouter les mods détectés ; les dépendances connues sont ajoutées avec eux.
4. Pour chaque source, renseigner l'auteur et la preuve d'autorisation ou la licence.
5. Vérifier l'ordre des mods et des cartes.
6. Construire localement et examiner `pack.lock.json` et `server-config.txt`.
7. Configurer SteamCMD, s'y authentifier une première fois manuellement, puis publier en visibilité privée.
8. Tester un serveur de staging avant de mettre l'item sur le serveur principal.
9. Appliquer le pack depuis l'écran Serveurs ; PZASM crée d'abord une sauvegarde du `.ini`.

## Mode CLI headless

Le CLI manipule les mêmes projets que l'UI. Chaque `project create` crée un pack global séparé qui recevra son propre Workshop ID ; `project publish` met ensuite ce même item à jour.

```bash
# inventaire local
dotnet run --project src/PZAdvancedServerManager.Cli -- scan

# créer et enrichir un pack
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Serveur principal"
dotnet run --project src/PZAdvancedServerManager.Cli -- project add --id <guid> --mod-id damnlib
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>

# programmer ce même pack depuis une session SSH
dotnet run --project src/PZAdvancedServerManager.Cli -- project configure --id <guid> --server servertest --automation true --schedule "04:00,16:00"

# publication volontaire uniquement
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes

# serveur Linux/Windows
dotnet run --project src/PZAdvancedServerManager.Cli -- server status --name servertest
dotnet run --project src/PZAdvancedServerManager.Cli -- server stop --name servertest --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- server set --name servertest --key MaxPlayers --value 32 --yes
```

Exécutez `pzasm help` ou le binaire CLI sans argument pour la liste complète. Les opérations de publication, d'arrêt et d'application exigent `--yes`. Rien ne se met à jour automatiquement sans activation explicite de l'administrateur.

## Droits et responsabilité

PZASM est un outil technique. Il ne donne aucun droit sur les mods inclus et ne transforme pas une redistribution non autorisée en utilisation permise.

La [politique officielle de modding Project Zomboid](https://projectzomboid.com/blog/modding-policy/) distingue :

- les packs publics, qui exigent l'autorisation de chaque auteur et une liste complète ;
- les packs semi-privés/non listés, qui exigent également les autorisations ;
- les copies strictement personnelles qui ne sont jamais publiées ni rendues téléchargeables.

Steam exige aussi l'acceptation de son [accord légal Workshop](https://steamcommunity.com/workshop/workshopsubmitinfo/). Le créateur et l'éditeur du pack sont seuls responsables des autorisations, crédits, licences et contenus tiers. LemonCorp et les contributeurs de PZASM ne sont pas responsables des packs construits ou publiés par les utilisateurs.

## Développement et tests

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

La CI GitHub exécute les tests puis produit deux artefacts autonomes (`win-x64` et `linux-x64`), chacun avec l'UI locale et le CLI headless. Lancez l'UI avec `--open-browser`. N'exposez pas le port PZASM à Internet : l'interface est un outil d'administration local et n'implémente pas d'authentification réseau.

Le projet cible .NET 9 et n'a pas besoin d'une base de données. Les écritures JSON sont atomiques et les builds sont préparés dans un dossier temporaire avant remplacement.

## Limites à connaître

- SteamCMD s'appuie sur la session Steam du compte ; PZASM ne stocke jamais le mot de passe ni le code Steam Guard.
- Le premier item peut rester masqué tant que l'accord légal Workshop n'est pas accepté.
- Une mise à jour d'un mod source peut modifier ses dépendances, Mod IDs ou cartes : un build planifié peut alors être bloqué par la validation.
- La fusion stricte ne tente pas de réécrire automatiquement les namespaces Lua, IDs de scripts, noms de textures, modèles, véhicules ou cartes. Une telle réécriture serait fragile et pourrait changer le comportement du mod.
- Les mods contenant des binaires ou extensions refusées par le validateur Workshop PZ sont bloqués.
