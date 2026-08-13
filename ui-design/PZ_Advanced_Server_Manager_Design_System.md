# PZ Advanced Server Manager — Design System UI

> Guide de référence destiné à Codex pour appliquer une identité visuelle cohérente à toute l’application web de gestion de serveurs et de mods Project Zomboid.
>
> La capture de design fournie avec ce document sert de **référence visuelle principale**. Ce fichier décrit les règles à respecter sur toutes les pages, y compris celles qui ne figurent pas dans la capture.

---

## 1. Objectif visuel

Créer une interface :

- professionnelle, crédible et adaptée à un outil d’administration ;
- sombre, mais très lisible ;
- légèrement inspirée de l’univers de Project Zomboid sans transformer l’application en interface de jeu ;
- dense juste ce qu’il faut pour afficher beaucoup d’informations ;
- cohérente sur les pages Serveurs, Mods, Modpacks, Workshop, Joueurs, Sauvegardes, Logs et Paramètres ;
- claire pour un utilisateur débutant, tout en restant efficace pour un administrateur avancé.

### Direction retenue

- Fond graphite sombre.
- Cartes légèrement plus claires que le fond.
- Accent principal ambre désaturé, utilisé avec modération.
- États techniques clairement différenciés : vert, orange, rouge et bleu.
- Bordures fines plutôt que grosses ombres.
- Rayons modérés : modernes, mais pas excessivement arrondis.
- Icônes simples et cohérentes.
- Aucune texture sale, aucun effet « métal rouillé », aucun néon.

---

## 2. Principes obligatoires

1. **La lisibilité prime sur l’esthétique.**
2. Une information importante ne doit jamais dépendre uniquement de sa couleur.
3. Les actions dangereuses doivent être visuellement distinctes et demander confirmation.
4. Les informations techniques doivent être regroupées, hiérarchisées et scannables rapidement.
5. Les mêmes composants doivent avoir le même rendu sur toutes les pages.
6. Les tableaux doivent rester exploitables avec beaucoup de lignes.
7. Les pages ne doivent pas devenir une accumulation de cartes flottantes.
8. Les animations doivent rester discrètes, rapides et fonctionnelles.
9. Ne pas réécrire la logique métier lors de l’application du design.
10. Ne pas remplacer le framework actuel sans nécessité technique réelle.

---

## 3. Palette de couleurs

Utiliser des variables CSS globales. Ne jamais disperser des couleurs en dur dans les composants.

```css
:root {
  color-scheme: dark;

  --bg-app: #0f1216;
  --bg-sidebar: #11151a;
  --bg-surface: #161b22;
  --bg-surface-raised: #1b222b;
  --bg-surface-hover: #202832;
  --bg-input: #12171d;

  --border-subtle: #252d37;
  --border-default: #303a46;
  --border-strong: #465260;

  --text-primary: #f2f4f7;
  --text-secondary: #b6bec9;
  --text-muted: #838e9c;
  --text-disabled: #626c78;
  --text-on-accent: #17130b;

  --accent: #d8a33f;
  --accent-hover: #e6b44f;
  --accent-soft: rgba(216, 163, 63, 0.13);
  --accent-border: rgba(216, 163, 63, 0.34);

  --success: #55b889;
  --success-soft: rgba(85, 184, 137, 0.13);
  --warning: #e2a94f;
  --warning-soft: rgba(226, 169, 79, 0.13);
  --danger: #dc6b6b;
  --danger-hover: #e77b7b;
  --danger-soft: rgba(220, 107, 107, 0.13);
  --info: #65a4dc;
  --info-soft: rgba(101, 164, 220, 0.13);

  --focus-ring: rgba(216, 163, 63, 0.42);
  --overlay: rgba(4, 7, 10, 0.72);

  --shadow-float: 0 14px 40px rgba(0, 0, 0, 0.26);
}
```

### Répartition des couleurs

- Ambre : action principale, élément actif, focus, accent de marque.
- Vert : serveur actif, opération réussie, synchronisation correcte.
- Orange : avertissement, redémarrage requis, mise à jour disponible.
- Rouge : serveur arrêté de façon anormale, erreur, suppression, action destructive.
- Bleu : information neutre, téléchargement, tâche en cours.

Ne pas utiliser l’ambre sur toutes les cartes. Il doit attirer l’attention sur les actions et états prioritaires.

---

## 4. Typographie

### Police

Préférence :

```css
font-family: Inter, Geist, ui-sans-serif, system-ui, -apple-system,
  BlinkMacSystemFont, "Segoe UI", sans-serif;
```

Ne pas imposer une police distante si le projet évite les dépendances externes. La pile système doit rester correcte.

Pour les logs, chemins, identifiants, ports, commandes et versions :

```css
font-family: "JetBrains Mono", "Cascadia Code", Consolas, monospace;
```

### Échelle typographique

| Usage | Taille | Graisse | Hauteur de ligne |
|---|---:|---:|---:|
| Titre de page | 26 px | 700 | 34 px |
| Titre de section | 18 px | 650 | 26 px |
| Titre de carte | 15 px | 650 | 22 px |
| Corps principal | 14 px | 400 | 21 px |
| Libellé / bouton | 13 px | 600 | 18 px |
| Aide / métadonnée | 12 px | 450 | 17 px |
| Log / code | 12–13 px | 400 | 19 px |

Règles :

- Ne pas descendre sous 12 px.
- Éviter les textes entièrement en majuscules.
- Utiliser la graisse et l’espacement avant d’augmenter la taille.
- Les valeurs importantes peuvent être plus grandes, mais jamais décoratives au détriment du contexte.

---

## 5. Espacement, dimensions et rayons

### Grille d’espacement

```css
--space-1: 4px;
--space-2: 8px;
--space-3: 12px;
--space-4: 16px;
--space-5: 20px;
--space-6: 24px;
--space-8: 32px;
--space-10: 40px;
```

### Rayons

```css
--radius-sm: 6px;
--radius-md: 9px;
--radius-lg: 12px;
--radius-pill: 999px;
```

### Dimensions communes

- Sidebar ouverte : 248 px.
- Sidebar réduite : 72 px.
- Topbar : 64 px.
- Hauteur des champs : 38 px.
- Bouton standard : 36–38 px.
- Bouton compact de tableau : 30–32 px.
- Icône standard : 18 px.
- Padding de carte : 18–20 px.
- Espace entre sections : 24 px.

---

## 6. Structure globale de l’application

```text
AppShell
├── Sidebar
│   ├── Brand
│   ├── PrimaryNavigation
│   ├── SecondaryNavigation
│   └── CurrentServerMiniStatus
├── MainArea
│   ├── Topbar
│   │   ├── Breadcrumb / page context
│   │   ├── Global search
│   │   └── Notifications / user menu
│   └── PageContent
│       ├── PageHeader
│       ├── OptionalTabsOrFilters
│       └── PageSections
└── GlobalLayers
    ├── Modal
    ├── Drawer
    ├── Toasts
    └── CommandPalette
```

### Sidebar

Navigation recommandée :

- Vue d’ensemble
- Serveurs
- Modpacks
- Workshop / Catalogue
- Joueurs
- Sauvegardes
- Tâches planifiées
- Logs
- Paramètres

Regrouper visuellement les entrées secondaires. L’entrée active utilise un fond `--accent-soft`, une bordure discrète ou un marqueur vertical ambre, et un texte principal clair.

### Topbar

Doit contenir uniquement les éléments globaux :

- fil d’Ariane ou nom du serveur courant ;
- recherche globale ;
- état des tâches ;
- notifications ;
- profil / paramètres rapides.

Ne pas dupliquer les actions propres à la page dans la topbar.

### Contenu principal

- Largeur fluide.
- Padding desktop : 24 à 32 px.
- Largeur maximale uniquement pour les pages de formulaire ; les dashboards et tableaux peuvent utiliser tout l’espace disponible.
- Les sections doivent rester alignées sur une grille commune.

---

## 7. Dashboard serveur de référence

Le dashboard principal doit permettre de comprendre l’état du serveur en quelques secondes.

### En-tête de page

Contenu :

- nom du serveur ;
- environnement ou canal ;
- statut lisible avec icône et texte ;
- adresse IP / port ;
- version du jeu ;
- dernière sauvegarde ;
- actions principales : Démarrer, Redémarrer, Arrêter, Console.

Les actions destructives ou à fort impact ne doivent pas avoir la même apparence que l’action principale.

### Première rangée de statistiques

Quatre à six cartes compactes maximum :

- joueurs connectés ;
- CPU ;
- mémoire ;
- durée de fonctionnement ;
- nombre de mods ;
- prochaine sauvegarde ou prochain redémarrage.

Chaque carte contient :

1. libellé ;
2. valeur principale ;
3. contexte secondaire ;
4. éventuellement une tendance ou barre de progression.

### Sections principales

- Courbes CPU / mémoire / joueurs.
- Joueurs actuellement connectés.
- Mods nécessitant une mise à jour.
- Activité récente.
- Tâches et opérations en cours.
- Événements importants ou alertes.

Ne pas afficher toutes les données détaillées sur le dashboard. Ajouter des liens « Voir tout » vers les pages spécialisées.

---

## 8. Composants

### 8.1 Cartes

```css
.ui-card {
  background: var(--bg-surface);
  border: 1px solid var(--border-subtle);
  border-radius: var(--radius-lg);
  box-shadow: none;
}

.ui-card:hover {
  border-color: var(--border-default);
}
```

Une carte interactive peut utiliser un léger fond au survol. Une carte purement informative ne doit pas bouger ni changer inutilement.

### 8.2 Boutons

Variantes obligatoires :

- `primary` : fond ambre, texte sombre ;
- `secondary` : fond surface, bordure visible ;
- `ghost` : sans fond, pour actions secondaires ;
- `danger` : rouge, uniquement pour action destructive ;
- `icon` : bouton carré avec tooltip obligatoire.

```css
.ui-button {
  min-height: 38px;
  padding: 0 14px;
  border-radius: var(--radius-md);
  border: 1px solid transparent;
  font-size: 13px;
  font-weight: 650;
  transition: background-color 140ms ease,
              border-color 140ms ease,
              color 140ms ease,
              transform 80ms ease;
}

.ui-button:active {
  transform: translateY(1px);
}

.ui-button:focus-visible {
  outline: 3px solid var(--focus-ring);
  outline-offset: 2px;
}
```

### 8.3 Champs et formulaires

- Libellé visible au-dessus du champ.
- Placeholder uniquement comme exemple, jamais comme unique libellé.
- Message d’aide sous le champ.
- Erreur sous le champ avec icône et texte.
- Regrouper les paramètres par blocs logiques.
- Utiliser un panneau latéral ou une page dédiée pour les formulaires longs.

```css
.ui-input,
.ui-select,
.ui-textarea {
  width: 100%;
  background: var(--bg-input);
  color: var(--text-primary);
  border: 1px solid var(--border-default);
  border-radius: var(--radius-md);
}

.ui-input:hover,
.ui-select:hover,
.ui-textarea:hover {
  border-color: var(--border-strong);
}

.ui-input:focus,
.ui-select:focus,
.ui-textarea:focus {
  border-color: var(--accent);
  outline: 3px solid var(--focus-ring);
  outline-offset: 0;
}
```

### 8.4 Badges et statuts

Toujours associer couleur, texte et éventuellement icône.

Exemples :

- ● En ligne
- ■ Arrêté
- ↻ Redémarrage
- ↑ Mise à jour disponible
- ✓ Synchronisé
- ! Erreur

Les badges doivent rester compacts et non cliquables sauf indication explicite.

### 8.5 Tableaux

Les pages Mods, Joueurs, Sauvegardes et Tâches utilisent des tableaux cohérents.

Règles :

- en-tête collant si la liste est longue ;
- première colonne importante toujours visible autant que possible ;
- lignes de 44 à 48 px ;
- survol discret ;
- sélection explicite par checkbox ;
- actions regroupées dans un menu ;
- tri visible ;
- filtres persistants pendant la session ;
- pagination ou virtualisation pour les grandes listes ;
- aucune bordure verticale lourde entre colonnes.

### 8.6 Onglets

Utiliser les onglets pour des vues proches, par exemple :

- Informations
- Configuration
- Mods
- Joueurs
- Sauvegardes
- Console
- Logs

L’onglet actif utilise un texte clair et une ligne ambre de 2 px. Ne pas enfermer chaque onglet dans une grosse pilule.

### 8.7 Modales

- Largeur standard : 480–620 px.
- Largeur avancée : 760–900 px.
- Titre précis.
- Description courte.
- Boutons alignés à droite.
- Focus piégé dans la modale.
- Fermeture par `Escape`, sauf opération critique en cours.

Les confirmations destructives doivent citer l’objet affecté :

> Supprimer le modpack « Vanilla Plus » ?

### 8.8 Tiroirs latéraux

Utiliser un drawer à droite pour :

- détails rapides d’un mod ;
- détails d’un joueur ;
- tâche en cours ;
- historique d’une sauvegarde ;
- inspection sans quitter la liste.

### 8.9 Toasts

- Position : bas à droite.
- Durée normale : 4 à 6 secondes.
- Erreurs importantes persistantes jusqu’à fermeture.
- Ne pas utiliser un toast comme seul retour pour une opération longue.

### 8.10 Logs et console

- Fond plus sombre que les cartes.
- Police monospace.
- Horodatage dans une colonne distincte.
- Niveaux `INFO`, `WARN`, `ERROR`, `DEBUG` visibles mais non agressifs.
- Recherche, pause, auto-scroll et copie.
- Possibilité de masquer les niveaux inutiles.
- Conserver une hauteur de ligne confortable.

---

## 9. Pages spécialisées

### Serveurs

- Vue en cartes compactes pour un petit nombre de serveurs.
- Vue tableau pour un grand nombre.
- Statut, joueurs, version, ressources et dernière activité visibles sans ouvrir le serveur.
- Action principale contextuelle : démarrer ou ouvrir.

### Mods

Colonnes recommandées :

- nom ;
- Workshop ID ;
- Mod ID ;
- version locale ;
- version distante ;
- état ;
- dépendances ;
- dernière vérification ;
- actions.

Une mise à jour disponible doit afficher :

- version actuelle ;
- nouvelle version ;
- date ;
- risque éventuel ;
- bouton d’inspection avant mise à jour.

### Modpacks

Présenter clairement :

- nom ;
- identifiant interne ;
- nombre de mods ;
- version du pack ;
- serveur(s) lié(s) ;
- date de build ;
- état de publication ;
- compatibilité.

Le flow de création peut utiliser un stepper :

1. Informations
2. Sélection des mods
3. Versions et dépendances
4. Validation
5. Build
6. Publication

### Workshop

- Barre de recherche large.
- Filtres dans un panneau compact.
- Résultats en liste ou grille sobre.
- Afficher auteur, Workshop ID, date de mise à jour, taille et dépendances.
- Éviter une grille de grandes images façon boutique si cela réduit la densité utile.

### Joueurs

- Recherche rapide.
- Statut connecté / hors ligne.
- Steam ID.
- Rôle.
- Dernière connexion.
- Temps de jeu.
- Actions : message, whitelist, permissions, expulsion, bannissement.

Les actions sensibles doivent être isolées dans un menu et confirmées.

### Sauvegardes

- Timeline ou tableau clair.
- Type : automatique, manuelle, avant mise à jour.
- Taille.
- Date.
- Serveur.
- Intégrité.
- Restauration avec confirmation et résumé de l’impact.

### Paramètres

- Navigation secondaire verticale.
- Groupes : Général, Chemins, SteamCMD, Blender/MCP si présent, Réseau, Sécurité, Notifications, Apparence, Avancé.
- Bouton Enregistrer collant uniquement sur les formulaires longs.
- Indiquer précisément les paramètres modifiés.

---

## 10. États de chargement et erreurs

Chaque page doit prévoir :

- chargement initial ;
- chargement partiel ;
- liste vide ;
- recherche sans résultat ;
- erreur réseau ;
- erreur serveur ;
- données obsolètes ;
- opération en cours ;
- opération réussie.

### Skeletons

Utiliser des skeletons proches de la structure finale. Éviter les spinners plein écran, sauf initialisation complète de l’application.

### État vide

Un bon état vide contient :

- un titre ;
- une phrase expliquant pourquoi la zone est vide ;
- une action pertinente ;
- éventuellement une petite illustration simple, pas une grande image décorative.

---

## 11. Responsive

### Desktop large — 1440 px et plus

- Sidebar ouverte.
- Dashboard sur grille 12 colonnes.
- Plusieurs panneaux visibles simultanément.

### Desktop compact / tablette paysage — 1024 à 1439 px

- Sidebar réduite ou repliable.
- Cartes statistiques en 2 ou 3 colonnes.
- Tableaux avec colonnes secondaires masquables.

### Mobile / tablette portrait — sous 768 px

- Navigation dans un drawer.
- Une seule colonne.
- En-tête de page empilé.
- Actions principales accessibles sans défilement horizontal.
- Tableaux transformés en listes structurées uniquement lorsque nécessaire.
- Les modales deviennent presque plein écran.

Ne pas tenter d’afficher toutes les colonnes d’un tableau sur mobile.

---

## 12. Accessibilité

- Contraste WCAG AA pour le texte courant.
- Focus visible sur tous les éléments interactifs.
- Navigation clavier complète.
- Labels explicites pour les icônes.
- `aria-live` pour les opérations et erreurs importantes.
- Zones cliquables d’au moins 36 × 36 px sur desktop et 44 × 44 px sur mobile.
- Respect de `prefers-reduced-motion`.
- Aucun statut indiqué uniquement par rouge/vert.

```css
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    scroll-behavior: auto !important;
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}
```

---

## 13. Animations

- Survol : 120–160 ms.
- Ouverture de drawer / modale : 180–220 ms.
- Aucun rebond décoratif.
- Aucun effet de parallaxe.
- Aucun glow permanent.
- Les changements de statut peuvent utiliser une courte transition, mais pas une pulsation infinie sauf tâche réellement active.

---

## 14. Icônes

Utiliser une seule bibliothèque d’icônes dans tout le projet, par exemple :

- Lucide ;
- Heroicons ;
- Bootstrap Icons si déjà présent.

Règles :

- style linéaire cohérent ;
- épaisseur identique ;
- taille standard 18 px ;
- tooltip sur les actions uniquement représentées par une icône ;
- ne pas mélanger emoji, SVG et polices d’icônes.

---

## 15. CSS de base recommandé

```css
* {
  box-sizing: border-box;
}

html,
body,
#app,
#root {
  min-height: 100%;
}

body {
  margin: 0;
  background: var(--bg-app);
  color: var(--text-primary);
  font-family: Inter, Geist, ui-sans-serif, system-ui, -apple-system,
    BlinkMacSystemFont, "Segoe UI", sans-serif;
  font-size: 14px;
  line-height: 1.5;
  text-rendering: optimizeLegibility;
  -webkit-font-smoothing: antialiased;
}

button,
input,
select,
textarea {
  font: inherit;
}

a {
  color: inherit;
}

::selection {
  background: var(--accent-soft);
  color: var(--text-primary);
}

::-webkit-scrollbar {
  width: 11px;
  height: 11px;
}

::-webkit-scrollbar-track {
  background: transparent;
}

::-webkit-scrollbar-thumb {
  background: #35404d;
  border: 3px solid transparent;
  border-radius: 999px;
  background-clip: padding-box;
}

::-webkit-scrollbar-thumb:hover {
  background: #465361;
  border: 3px solid transparent;
  background-clip: padding-box;
}
```

---

## 16. Architecture de composants recommandée

Adapter les noms au framework existant.

```text
/components/ui
  Button
  IconButton
  Card
  Badge
  Input
  Select
  Checkbox
  Switch
  Tabs
  Tooltip
  DropdownMenu
  Modal
  Drawer
  Toast
  DataTable
  EmptyState
  Skeleton
  Progress
  ConfirmDialog

/components/layout
  AppShell
  Sidebar
  SidebarItem
  Topbar
  PageHeader
  SectionHeader
  ContentGrid

/components/server
  ServerStatusBadge
  ServerActionBar
  ServerMetricCard
  ResourceChart
  PlayerList
  ActiveTaskPanel

/components/mods
  ModStatusBadge
  ModTable
  ModDetailsDrawer
  DependencyList
  UpdateComparison
```

Éviter les composants géants contenant logique métier, appels réseau et rendu complet de page dans un seul fichier.

---

## 17. Règles d’implémentation pour Codex

Codex doit procéder dans cet ordre :

1. Inventorier les routes, layouts, feuilles de style et composants déjà présents.
2. Identifier le framework UI actuel et le conserver.
3. Créer les tokens globaux de couleur, espacement, typographie et rayon.
4. Créer ou normaliser les composants UI partagés.
5. Appliquer le nouveau `AppShell` : sidebar, topbar et contenu principal.
6. Migrer d’abord le dashboard principal comme page de référence.
7. Migrer ensuite les pages de listes et tableaux.
8. Migrer les formulaires et paramètres.
9. Uniformiser les modales, drawers, notifications et confirmations.
10. Ajouter les états de chargement, vide, erreur et succès manquants.
11. Vérifier le responsive à 1920, 1440, 1280, 1024, 768 et 390 px.
12. Vérifier clavier, focus, contraste et lecteurs d’écran.
13. Comparer visuellement toutes les pages avec la capture de référence.
14. Ne supprimer aucune fonctionnalité existante.
15. Ne modifier les contrats API ou modèles de données que si une correction séparée est indispensable.

---

## 18. Prompt prêt à donner à Codex

```text
Tu dois appliquer à toute l’application web PZ Advanced Server Manager le design system décrit dans le fichier `PZ_Advanced_Server_Manager_Design_System.md` et reproduire fidèlement la direction visuelle de la capture de référence fournie.

Objectifs prioritaires :
- interface sombre graphite, professionnelle et très lisible ;
- accent ambre discret ;
- composants cohérents sur toutes les pages ;
- excellente densité d’information sans surcharge visuelle ;
- navigation claire pour Serveurs, Mods, Modpacks, Workshop, Joueurs, Sauvegardes, Logs et Paramètres ;
- responsive complet ;
- accessibilité clavier et contrastes corrects.

Contraintes :
- conserve le framework, l’architecture générale et la logique métier existants ;
- ne casse aucune route, aucun appel API, aucun formulaire ni workflow ;
- n’introduis pas de nouvelle bibliothèque lourde si le projet possède déjà des composants ou une solution CSS exploitable ;
- centralise toutes les couleurs, dimensions et espacements dans des tokens globaux ;
- remplace progressivement les styles locaux incohérents par des composants partagés ;
- ne fais pas une simple recoloration : améliore également la hiérarchie, les espacements, les tableaux, les formulaires, les états de chargement, les erreurs, les modales et le responsive ;
- évite les effets gaming exagérés, le néon, les textures, les grosses ombres et les rayons excessifs ;
- utilise une seule bibliothèque d’icônes ;
- les actions dangereuses doivent être distinctes et confirmées ;
- aucun statut ne doit dépendre uniquement de la couleur.

Méthode :
1. Analyse d’abord tout le projet et dresse l’inventaire des pages et composants.
2. Crée les tokens et composants de base.
3. Implémente l’AppShell global.
4. Refonte le dashboard serveur comme référence.
5. Étends ensuite le système aux autres pages sans dupliquer les styles.
6. Effectue une passe responsive et accessibilité.
7. Lance les tests et le build existants après chaque lot important.
8. Corrige les régressions avant de continuer.

À la fin, fournis :
- la liste des fichiers modifiés ;
- les composants partagés créés ;
- les pages migrées ;
- les éventuelles limites restantes ;
- les commandes de test exécutées et leurs résultats.
```

---

## 19. Critères d’acceptation

Le travail est accepté lorsque :

- toutes les pages utilisent les mêmes tokens ;
- la sidebar et la topbar sont communes ;
- les boutons, champs, badges, cartes et tableaux sont uniformes ;
- aucun texte important n’est trop faible ou trop petit ;
- les tableaux restent utilisables avec beaucoup de données ;
- les états serveur et mod sont compréhensibles sans se fier uniquement à la couleur ;
- l’interface fonctionne correctement au clavier ;
- le responsive ne produit aucun défilement horizontal global ;
- les actions destructives demandent confirmation ;
- l’aspect général correspond à la capture : sombre, précis, moderne, sobre et lisible ;
- le build et les tests existants passent sans régression fonctionnelle.

---

## 20. Éléments à éviter absolument

- Noir pur `#000000` comme fond général.
- Texte gris trop faible.
- Grandes surfaces ambre.
- Boutons entièrement arrondis partout.
- Cartes avec de très grosses ombres.
- Animations lentes.
- Tableaux avec de lourdes bordures verticales.
- Icônes sans tooltip.
- Formulaires sans libellé.
- Messages d’erreur uniquement dans un toast.
- Menu à trois points pour l’unique action principale d’une page.
- Trop de graphiques décoratifs.
- Thème « apocalypse » excessif.
- Copie visuelle exacte de l’interface du jeu Project Zomboid.

