(() => {
    const languages = ['fr', 'en', 'es', 'de', 'pt-BR', 'zh-CN'];
    const rows = [
        ['Packs', 'Packs', 'Packs', 'Packs', 'Pacotes', '模组包'],
        ['Serveurs', 'Servers', 'Servidores', 'Server', 'Servidores', '服务器'],
        ['Mode clair', 'Light mode', 'Modo claro', 'Heller Modus', 'Modo claro', '浅色模式'],
        ['Mode sombre', 'Dark mode', 'Modo oscuro', 'Dunkler Modus', 'Modo escuro', '深色模式'],
        ['Changer le thème', 'Change theme', 'Cambiar tema', 'Design ändern', 'Alterar tema', '切换主题'],
        ['Langue de l’interface', 'Interface language', 'Idioma de la interfaz', 'Oberflächensprache', 'Idioma da interface', '界面语言'],
        ['Préférences d’affichage', 'Display preferences', 'Preferencias de visualización', 'Anzeigeeinstellungen', 'Preferências de exibição', '显示偏好'],
        ['Langue', 'Language', 'Idioma', 'Sprache', 'Idioma', '语言'],
        ['OPÉRATION EN COURS', 'OPERATION IN PROGRESS', 'OPERACIÓN EN CURSO', 'VORGANG LÄUFT', 'OPERAÇÃO EM ANDAMENTO', '操作进行中'],
        ['Chargement…', 'Loading…', 'Cargando…', 'Wird geladen…', 'Carregando…', '正在加载…'],
        ['Veuillez patienter quelques instants.', 'Please wait a moment.', 'Espera un momento.', 'Bitte einen Moment warten.', 'Aguarde um momento.', '请稍候。'],
        ['Préparation…', 'Preparing…', 'Preparando…', 'Vorbereitung…', 'Preparando…', '正在准备…'],
        ['Fermer', 'Close', 'Cerrar', 'Schließen', 'Fechar', '关闭'],
        ['Vos serveurs. Vos versions. Votre cadence.', 'Your servers. Your versions. Your schedule.', 'Tus servidores. Tus versiones. Tu ritmo.', 'Deine Server. Deine Versionen. Dein Zeitplan.', 'Seus servidores. Suas versões. Seu ritmo.', '你的服务器、版本与节奏。'],
        ["Regroupez les mods d'un serveur sous un seul Workshop ID, conservez chaque Mod ID intact et choisissez vous-même le moment des mises à jour.", 'Group a server’s mods under one Workshop ID, keep every Mod ID intact, and choose when updates happen.', 'Agrupa los mods de un servidor bajo un solo Workshop ID, conserva cada Mod ID y decide cuándo actualizar.', 'Fasse die Mods eines Servers unter einer Workshop-ID zusammen, behalte jede Mod-ID bei und bestimme den Update-Zeitpunkt.', 'Agrupe os mods de um servidor em um único Workshop ID, mantenha cada Mod ID e escolha quando atualizar.', '将服务器模组集中到一个 Workshop ID 下，保留每个 Mod ID，并自行决定更新时间。'],
        ["Responsabilité de l'éditeur du pack", 'Pack publisher responsibility', 'Responsabilidad del editor del paquete', 'Verantwortung des Paket-Herausgebers', 'Responsabilidade do editor do pacote', '模组包发布者责任'],
        ["PZ Advanced Server Manager ne donne aucun droit de redistribution. Même pour un pack non listé ou réservé à un serveur, vérifiez l'autorisation de chaque auteur. LemonCorp n'est pas responsable des packs créés par les utilisateurs.", 'PZ Advanced Server Manager grants no redistribution rights. Even for an unlisted or server-only pack, verify every author’s permission. LemonCorp is not responsible for user-created packs.', 'PZ Advanced Server Manager no concede derechos de redistribución. Incluso para un paquete no listado o privado, verifica el permiso de cada autor. LemonCorp no se responsabiliza de los paquetes creados por usuarios.', 'PZ Advanced Server Manager gewährt keine Weiterverteilungsrechte. Prüfe auch bei nicht gelisteten oder servergebundenen Paketen die Erlaubnis jedes Autors. LemonCorp haftet nicht für Benutzerpakete.', 'O PZ Advanced Server Manager não concede direitos de redistribuição. Mesmo para pacotes não listados ou privados, verifique a permissão de cada autor. A LemonCorp não se responsabiliza por pacotes de usuários.', 'PZ Advanced Server Manager 不授予再分发权。即使是非公开或服务器专用模组包，也请确认每位作者的许可。LemonCorp 不对用户创建的模组包负责。'],
        ['INSTALLATION LOCALE', 'LOCAL INSTALLATION', 'INSTALACIÓN LOCAL', 'LOKALE INSTALLATION', 'INSTALAÇÃO LOCAL', '本地安装'],
        ['Project Zomboid détecté', 'Project Zomboid detected', 'Project Zomboid detectado', 'Project Zomboid erkannt', 'Project Zomboid detectado', '已检测到 Project Zomboid'],
        ['Actualiser', 'Refresh', 'Actualizar', 'Aktualisieren', 'Atualizar', '刷新'],
        ['Jeu', 'Game', 'Juego', 'Spiel', 'Jogo', '游戏'],
        ['Serveur dédié', 'Dedicated server', 'Servidor dedicado', 'Dedicated Server', 'Servidor dedicado', '专用服务器'],
        ['Non détecté', 'Not detected', 'No detectado', 'Nicht erkannt', 'Não detectado', '未检测到'],
        ['Non installé', 'Not installed', 'No instalado', 'Nicht installiert', 'Não instalado', '未安装'],
        ['Données PZASM', 'PZASM data', 'Datos de PZASM', 'PZASM-Daten', 'Dados do PZASM', 'PZASM 数据'],
        ['Gérer les serveurs', 'Manage servers', 'Gestionar servidores', 'Server verwalten', 'Gerenciar servidores', '管理服务器'],
        ['Vérifier SteamCMD', 'Check SteamCMD', 'Comprobar SteamCMD', 'SteamCMD prüfen', 'Verificar SteamCMD', '检查 SteamCMD'],
        ['Installer SteamCMD', 'Install SteamCMD', 'Instalar SteamCMD', 'SteamCMD installieren', 'Instalar SteamCMD', '安装 SteamCMD'],
        ['NOUVEAU PROJET', 'NEW PROJECT', 'NUEVO PROYECTO', 'NEUES PROJEKT', 'NOVO PROJETO', '新建项目'],
        ['Créer un pack stable', 'Create a stable pack', 'Crear un paquete estable', 'Stabiles Paket erstellen', 'Criar um pacote estável', '创建稳定模组包'],
        ['Le suffixe unique du projet restera identique à chaque réouverture et mise à jour.', 'The project’s unique suffix remains stable across every reopen and update.', 'El sufijo único del proyecto se conserva al reabrirlo y actualizarlo.', 'Das eindeutige Projektsuffix bleibt beim Öffnen und Aktualisieren erhalten.', 'O sufixo exclusivo do projeto permanece igual ao reabrir e atualizar.', '项目唯一后缀在重新打开和更新时保持不变。'],
        ['Nom du pack', 'Pack name', 'Nombre del paquete', 'Paketname', 'Nome do pacote', '模组包名称'],
        ['Créer le pack', 'Create pack', 'Crear paquete', 'Paket erstellen', 'Criar pacote', '创建模组包'],
        ['PROJETS ENREGISTRÉS', 'SAVED PROJECTS', 'PROYECTOS GUARDADOS', 'GESPEICHERTE PROJEKTE', 'PROJETOS SALVOS', '已保存项目'],
        ['Reprendre ou mettre à jour un pack', 'Resume or update a pack', 'Continuar o actualizar un paquete', 'Paket fortsetzen oder aktualisieren', 'Retomar ou atualizar um pacote', '继续或更新模组包'],
        ['Aucun pack pour le moment', 'No packs yet', 'Aún no hay paquetes', 'Noch keine Pakete', 'Nenhum pacote ainda', '暂无模组包'],
        ['Dupliquer', 'Duplicate', 'Duplicar', 'Duplizieren', 'Duplicar', '复制'],
        ['Supprimer', 'Delete', 'Eliminar', 'Löschen', 'Excluir', '删除'],
        ['BUNDLE CONSEILLÉ', 'RECOMMENDED BUNDLE', 'BUNDLE RECOMENDADO', 'EMPFOHLENES BUNDLE', 'BUNDLE RECOMENDADO', '推荐 BUNDLE'],
        ['FUSION STRICTE', 'STRICT FUSION', 'FUSIÓN ESTRICTA', 'STRENGE FUSION', 'FUSÃO ESTRITA', '严格融合'],
        ['à créer', 'to create', 'por crear', 'zu erstellen', 'a criar', '待创建'],
        ['Tableau de bord', 'Dashboard', 'Panel principal', 'Übersicht', 'Painel', '控制面板'],
        ['Catalogue de mods', 'Mod catalog', 'Catálogo de mods', 'Mod-Katalog', 'Catálogo de mods', '模组目录'],
        ['Explorez le Workshop sans quitter le manager, ou sélectionnez les mods déjà présents dans vos installations Project Zomboid.', 'Browse the Workshop without leaving the manager, or select mods already present in your Project Zomboid installations.', 'Explora el Workshop sin salir del gestor o selecciona mods ya instalados en Project Zomboid.', 'Durchsuche den Workshop direkt im Manager oder wähle bereits installierte Project-Zomboid-Mods.', 'Explore o Workshop sem sair do gerenciador ou selecione mods já instalados no Project Zomboid.', '无需离开管理器即可浏览创意工坊，或选择 Project Zomboid 安装中已有的模组。'],
        ['Recherche', 'Search', 'Buscar', 'Suche', 'Pesquisar', '搜索'],
        ['Détails', 'Details', 'Detalles', 'Details', 'Detalhes', '详情'],
        ['Import', 'Import', 'Importar', 'Import', 'Importar', '导入'],
        ['Workshop public', 'Public Workshop', 'Workshop público', 'Öffentlicher Workshop', 'Workshop público', '公开创意工坊'],
        ['Recherche, tendances et téléchargement', 'Search, trends and downloads', 'Búsqueda, tendencias y descargas', 'Suche, Trends und Downloads', 'Pesquisa, tendências e downloads', '搜索、趋势与下载'],
        ['Installés localement', 'Installed locally', 'Instalados localmente', 'Lokal installiert', 'Instalados localmente', '本地已安装'],
        ['Client, Dedicated Server et cache Workshop', 'Client, Dedicated Server and Workshop cache', 'Cliente, servidor dedicado y caché de Workshop', 'Client, Dedicated Server und Workshop-Cache', 'Cliente, servidor dedicado e cache do Workshop', '客户端、专用服务器与创意工坊缓存'],
        ['API publique Steam', 'Public Steam API', 'API pública de Steam', 'Öffentliche Steam-API', 'API pública da Steam', 'Steam 公共 API'],
        ['Classement', 'Sort', 'Orden', 'Sortierung', 'Ordenação', '排序'],
        ['Tendances', 'Trending', 'Tendencias', 'Trends', 'Em alta', '趋势'],
        ['Plus récents', 'Newest', 'Más recientes', 'Neueste', 'Mais recentes', '最新'],
        ['Plus abonnés', 'Most subscribed', 'Más suscritos', 'Meiste Abonnenten', 'Mais inscritos', '订阅最多'],
        ['Plus consultés', 'Most viewed', 'Más vistos', 'Meist angesehen', 'Mais visualizados', '浏览最多'],
        ['Pertinence', 'Relevance', 'Relevancia', 'Relevanz', 'Relevância', '相关性'],
        ['Tag exact', 'Exact tag', 'Etiqueta exacta', 'Exakter Tag', 'Tag exata', '精确标签'],
        ['Rechercher', 'Search', 'Buscar', 'Suchen', 'Pesquisar', '搜索'],
        ['Tout sélectionner sur cette page', 'Select all on this page', 'Seleccionar todo en esta página', 'Alles auf dieser Seite auswählen', 'Selecionar tudo nesta página', '选择本页全部'],
        ['Désélectionner la page', 'Deselect this page', 'Deseleccionar esta página', 'Auswahl dieser Seite aufheben', 'Desmarcar esta página', '取消选择本页'],
        ['Vider toute la sélection', 'Clear entire selection', 'Vaciar toda la selección', 'Gesamte Auswahl leeren', 'Limpar toda a seleção', '清空全部选择'],
        ['Aucun mod sélectionné pour le moment.', 'No mods selected yet.', 'Aún no hay mods seleccionados.', 'Noch keine Mods ausgewählt.', 'Nenhum mod selecionado ainda.', '尚未选择模组。'],
        ['Ajouter la sélection', 'Add selection', 'Añadir selección', 'Auswahl hinzufügen', 'Adicionar seleção', '添加所选项'],
        ['Ajouter et analyser', 'Add and analyze', 'Añadir y analizar', 'Hinzufügen und analysieren', 'Adicionar e analisar', '添加并分析'],
        ['Page précédente', 'Previous page', 'Página anterior', 'Vorherige Seite', 'Página anterior', '上一页'],
        ['Page suivante', 'Next page', 'Página siguiente', 'Nächste Seite', 'Próxima página', '下一页'],
        ['Voir la fiche Steam', 'View on Steam', 'Ver en Steam', 'Auf Steam ansehen', 'Ver na Steam', '在 Steam 查看'],
        ['SÉLECTIONNER', 'SELECT', 'SELECCIONAR', 'AUSWÄHLEN', 'SELECIONAR', '选择'],
        ['BIBLIOTHÈQUE DÉTECTÉE', 'DETECTED LIBRARY', 'BIBLIOTECA DETECTADA', 'ERKANNTE BIBLIOTHEK', 'BIBLIOTECA DETECTADA', '已检测资源库'],
        ['WORKSHOP PROJECT ZOMBOID', 'PROJECT ZOMBOID WORKSHOP', 'WORKSHOP DE PROJECT ZOMBOID', 'PROJECT-ZOMBOID-WORKSHOP', 'WORKSHOP DO PROJECT ZOMBOID', 'PROJECT ZOMBOID 创意工坊'],
        ['DÉJÀ AJOUTÉ', 'ALREADY ADDED', 'YA AÑADIDO', 'BEREITS HINZUGEFÜGT', 'JÁ ADICIONADO', '已添加'],
        ['Retirer', 'Remove', 'Quitar', 'Entfernen', 'Remover', '移除'],
        ['mod(s) sélectionné(s)', 'mod(s) selected', 'mod(s) seleccionado(s)', 'Mod(s) ausgewählt', 'mod(s) selecionado(s)', '个模组已选择'],
        ['Version du mod', 'Mod version', 'Versión del mod', 'Mod-Version', 'Versão do mod', '模组版本'],
        ['Compatibilité PZ', 'PZ compatibility', 'Compatibilidad PZ', 'PZ-Kompatibilität', 'Compatibilidade PZ', 'PZ 兼容性'],
        ['Révision figée', 'Pinned revision', 'Revisión fijada', 'Fixierte Revision', 'Revisão fixada', '固定修订版'],
        ['Version non déclarée', 'Version not declared', 'Versión no declarada', 'Version nicht angegeben', 'Versão não declarada', '未声明版本'],
        ['Afficher les détails', 'Show details', 'Mostrar detalles', 'Details anzeigen', 'Mostrar detalhes', '显示详情'],
        ['Masquer les détails', 'Hide details', 'Ocultar detalles', 'Details ausblenden', 'Ocultar detalhes', '隐藏详情'],
        ['Mods & droits', 'Mods & rights', 'Mods y derechos', 'Mods & Rechte', 'Mods e direitos', '模组与权限'],
        ['Mods, dépendances et autorisations', 'Mods, dependencies and permissions', 'Mods, dependencias y permisos', 'Mods, Abhängigkeiten und Berechtigungen', 'Mods, dependências e permissões', '模组、依赖与权限'],
        ['Chaque source est copiée dans un snapshot privé avant d’entrer dans un build.', 'Each source is copied to a private snapshot before entering a build.', 'Cada fuente se copia en una instantánea privada antes de compilar.', 'Jede Quelle wird vor dem Build in einen privaten Snapshot kopiert.', 'Cada fonte é copiada para um snapshot privado antes do build.', '每个来源在构建前都会复制到私有快照。'],
        ['Ouvrir le catalogue interne', 'Open internal catalog', 'Abrir catálogo interno', 'Internen Katalog öffnen', 'Abrir catálogo interno', '打开内部目录'],
        ['Relancer la détection', 'Run detection again', 'Repetir detección', 'Erkennung erneut starten', 'Executar detecção novamente', '重新检测'],
        ['Explorer le Workshop', 'Browse Workshop', 'Explorar Workshop', 'Workshop durchsuchen', 'Explorar Workshop', '浏览创意工坊'],
        ['Choisir les mods installés', 'Choose installed mods', 'Elegir mods instalados', 'Installierte Mods wählen', 'Escolher mods instalados', '选择已安装模组'],
        ['Enregistrer les droits', 'Save rights', 'Guardar derechos', 'Rechte speichern', 'Salvar direitos', '保存权限'],
        ['Origine', 'Source', 'Origen', 'Quelle', 'Origem', '来源'],
        ['Snapshot', 'Snapshot', 'Instantánea', 'Snapshot', 'Snapshot', '快照'],
        ['Dépendances', 'Dependencies', 'Dependencias', 'Abhängigkeiten', 'Dependências', '依赖'],
        ['Cartes', 'Maps', 'Mapas', 'Karten', 'Mapas', '地图'],
        ['En attente', 'Pending', 'Pendiente', 'Ausstehend', 'Pendente', '等待中'],
        ['FIGÉ', 'PINNED', 'FIJADO', 'FIXIERT', 'FIXADO', '已固定'],
        ['Unknown', 'Unknown', 'Desconocido', 'Unbekannt', 'Desconhecido', '未知'],
        ['AuthorOwned', 'Author owned', 'Propiedad del autor', 'Eigener Autor', 'Do próprio autor', '作者自有'],
        ['ExplicitPermission', 'Explicit permission', 'Permiso explícito', 'Explizite Erlaubnis', 'Permissão explícita', '明确许可'],
        ['CompatibleLicense', 'Compatible license', 'Licencia compatible', 'Kompatible Lizenz', 'Licença compatível', '兼容许可证'],
        ['Denied', 'Denied', 'Denegado', 'Abgelehnt', 'Negado', '已拒绝'],
        ['Construire', 'Build', 'Construir', 'Erstellen', 'Construir', '构建'],
        ['Mettre à jour les mods', 'Update mods', 'Actualizar mods', 'Mods aktualisieren', 'Atualizar mods', '更新模组'],
        ['Publier', 'Publish', 'Publicar', 'Veröffentlichen', 'Publicar', '发布'],
        ['COMMANDES PRINCIPALES', 'PRIMARY COMMANDS', 'COMANDOS PRINCIPALES', 'HAUPTBEFEHLE', 'COMANDOS PRINCIPAIS', '主要命令'],
        ['Générer le pack depuis les versions figées', 'Generate the pack from pinned versions', 'Generar el paquete desde versiones fijadas', 'Paket aus fixierten Versionen erzeugen', 'Gerar o pacote com as versões fixadas', '从固定版本生成模组包'],
        ['Mettre à jour l’unique Workshop ID', 'Update the single Workshop ID', 'Actualizar el único Workshop ID', 'Die einzige Workshop-ID aktualisieren', 'Atualizar o único Workshop ID', '更新唯一的 Workshop ID'],
        ['MÀJ GLOBALE', 'GLOBAL UPDATE', 'ACT. GLOBAL', 'GLOBAL AKTUALISIERT', 'ATUAL. GLOBAL', '全局更新'],
        ['MÀJ MANUELLE', 'MANUAL UPDATE', 'ACT. MANUAL', 'MANUELL', 'ATUAL. MANUAL', '手动更新'],
        ['Inclure dans « Mettre à jour les mods »', 'Include in “Update mods”', 'Incluir en «Actualizar mods»', 'In „Mods aktualisieren“ einbeziehen', 'Incluir em “Atualizar mods”', '包含在“更新模组”中'],
        ['Désactivez cette option pour conserver indéfiniment la révision figée. La mise à jour individuelle reste disponible.', 'Disable this to keep the pinned revision indefinitely. Individual update remains available.', 'Desactiva esta opción para conservar indefinidamente la revisión fijada. La actualización individual seguirá disponible.', 'Deaktivieren, um die fixierte Revision dauerhaft zu behalten. Die einzelne Aktualisierung bleibt verfügbar.', 'Desative para manter a revisão fixada indefinidamente. A atualização individual continuará disponível.', '关闭后将永久保留固定修订，仍可单独更新。'],
        ['Enregistrer les droits et la mise à jour', 'Save rights and update policy', 'Guardar derechos y actualización', 'Rechte und Aktualisierung speichern', 'Salvar direitos e atualização', '保存权限与更新策略'],
        ['Mettre à jour ce mod', 'Update this mod', 'Actualizar este mod', 'Diesen Mod aktualisieren', 'Atualizar este mod', '更新此模组'],
        ['Non déclaré dans mod.info', 'Not declared in mod.info', 'No declarado en mod.info', 'Nicht in mod.info angegeben', 'Não declarado no mod.info', 'mod.info 中未声明'],
        ['Aucun auteur déclaré par le mod; vérifiez la fiche Workshop.', 'No author is declared by the mod; check its Workshop page.', 'El mod no declara autor; consulta su página de Workshop.', 'Der Mod nennt keinen Autor; Workshop-Seite prüfen.', 'O mod não declara autor; verifique a página do Workshop.', '模组未声明作者；请检查创意工坊页面。'],
        ['CONFIRMATION REQUISE', 'CONFIRMATION REQUIRED', 'CONFIRMACIÓN REQUERIDA', 'BESTÄTIGUNG ERFORDERLICH', 'CONFIRMAÇÃO NECESSÁRIA', '需要确认'],
        ['Confirmer l’opération', 'Confirm operation', 'Confirmar operación', 'Vorgang bestätigen', 'Confirmar operação', '确认操作'],
        ['Vérifiez les conséquences avant de continuer.', 'Review the consequences before continuing.', 'Revisa las consecuencias antes de continuar.', 'Vor dem Fortfahren die Folgen prüfen.', 'Revise as consequências antes de continuar.', '继续前请检查操作影响。'],
        ['Annuler', 'Cancel', 'Cancelar', 'Abbrechen', 'Cancelar', '取消'],
        ['Confirmer', 'Confirm', 'Confirmar', 'Bestätigen', 'Confirmar', '确认'],
        ['Mettre à jour les mods ?', 'Update mods?', '¿Actualizar mods?', 'Mods aktualisieren?', 'Atualizar mods?', '更新模组？'],
        ['Publier le pack ?', 'Publish the pack?', '¿Publicar el paquete?', 'Paket veröffentlichen?', 'Publicar o pacote?', '发布模组包？'],
        ['Publier maintenant', 'Publish now', 'Publicar ahora', 'Jetzt veröffentlichen', 'Publicar agora', '立即发布'],
        ['Mettre à jour ce mod ?', 'Update this mod?', '¿Actualizar este mod?', 'Diesen Mod aktualisieren?', 'Atualizar este mod?', '更新此模组？'],
        ['Retirer ce mod ?', 'Remove this mod?', '¿Quitar este mod?', 'Diesen Mod entfernen?', 'Remover este mod?', '移除此模组？'],
        ['Retirer du pack', 'Remove from pack', 'Quitar del paquete', 'Aus Paket entfernen', 'Remover do pacote', '从模组包移除'],
        ['Supprimer ce projet ?', 'Delete this project?', '¿Eliminar este proyecto?', 'Dieses Projekt löschen?', 'Excluir este projeto?', '删除此项目？'],
        ['Supprimer le projet', 'Delete project', 'Eliminar proyecto', 'Projekt löschen', 'Excluir projeto', '删除项目'],
        ['Sauvegarder et arrêter le serveur ?', 'Save and stop the server?', '¿Guardar y detener el servidor?', 'Server speichern und stoppen?', 'Salvar e parar o servidor?', '保存并停止服务器？'],
        ['Sauvegarder et arrêter', 'Save and stop', 'Guardar y detener', 'Speichern und stoppen', 'Salvar e parar', '保存并停止'],
        ['Appliquer ce pack au serveur ?', 'Apply this pack to the server?', '¿Aplicar este paquete al servidor?', 'Dieses Paket auf den Server anwenden?', 'Aplicar este pacote ao servidor?', '将此模组包应用到服务器？'],
        ['Appliquer le pack', 'Apply pack', 'Aplicar paquete', 'Paket anwenden', 'Aplicar pacote', '应用模组包'],
        ['Enregistrer l’INI brut ?', 'Save raw INI?', '¿Guardar el INI sin procesar?', 'INI-Rohdaten speichern?', 'Salvar INI bruto?', '保存原始 INI？'],
        ['Enregistrer l’INI', 'Save INI', 'Guardar INI', 'INI speichern', 'Salvar INI', '保存 INI'],
        ['Les snapshots des mods marqués « Mise à jour globale » seront remplacés. Les mods exclus resteront strictement inchangés et aucun publish ne sera effectué.', 'Snapshots for mods marked “Global update” will be replaced. Excluded mods remain strictly unchanged and nothing will be published.', 'Se reemplazarán las instantáneas de los mods marcados «Actualización global». Los mods excluidos no cambiarán y no se publicará nada.', 'Snapshots der als „Global aktualisieren“ markierten Mods werden ersetzt. Ausgeschlossene Mods bleiben unverändert; es wird nichts veröffentlicht.', 'Os snapshots dos mods marcados como “Atualização global” serão substituídos. Mods excluídos permanecerão inalterados e nada será publicado.', '标记为“全局更新”的模组快照将被替换。排除的模组保持不变，且不会发布。'],
        ['Le pack sera construit puis publié ou mis à jour sur Steam Workshop. Si un serveur coordonné est en ligne, il sera sauvegardé, arrêté proprement puis redémarré.', 'The pack will be built and then published or updated on Steam Workshop. If a coordinated server is online, it will be saved, stopped cleanly, and restarted.', 'El paquete se compilará y después se publicará o actualizará en Steam Workshop. Si hay un servidor coordinado en línea, se guardará, detendrá correctamente y reiniciará.', 'Das Paket wird erstellt und anschließend im Steam Workshop veröffentlicht oder aktualisiert. Ein laufender koordinierter Server wird gespeichert, sauber gestoppt und neu gestartet.', 'O pacote será compilado e publicado ou atualizado no Steam Workshop. Se um servidor coordenado estiver online, será salvo, encerrado corretamente e reiniciado.', '模组包将构建并发布或更新到 Steam 创意工坊。若协调服务器在线，将先保存、正常停止并重新启动。'],
        ['Le projet, ses snapshots et ses builds locaux seront supprimés. Le Workshop publié et les mods originaux ne seront pas modifiés.', 'The project, its snapshots, and local builds will be deleted. The published Workshop item and original mods will not be changed.', 'Se eliminarán el proyecto, sus instantáneas y compilaciones locales. El elemento publicado y los mods originales no se modificarán.', 'Projekt, Snapshots und lokale Builds werden gelöscht. Das veröffentlichte Workshop-Item und Original-Mods bleiben unverändert.', 'O projeto, snapshots e builds locais serão excluídos. O item publicado e os mods originais não serão alterados.', '项目、快照和本地构建将被删除。已发布的创意工坊条目和原始模组不会更改。'],
        ['Le gestionnaire enverra save puis quit par RCON et attendra l’arrêt complet. Aucun arrêt forcé ne sera utilisé.', 'The manager will send save then quit through RCON and wait for a complete shutdown. No forced stop will be used.', 'El gestor enviará save y luego quit mediante RCON y esperará el cierre completo. No se forzará la detención.', 'Der Manager sendet per RCON save und danach quit und wartet auf das vollständige Herunterfahren. Kein erzwungener Stopp.', 'O gerenciador enviará save e depois quit via RCON e aguardará o encerramento completo. Nenhuma parada forçada será usada.', '管理器将通过 RCON 依次发送 save 和 quit，并等待完全停止，不会强制终止。'],
        ['Les listes WorkshopItems, Mods et Map seront remplacées après création d’une sauvegarde horodatée. Les autres réglages du serveur resteront inchangés.', 'WorkshopItems, Mods, and Map lists will be replaced after a timestamped backup is created. Other server settings remain unchanged.', 'Las listas WorkshopItems, Mods y Map se reemplazarán después de crear una copia con fecha. Los demás ajustes no cambiarán.', 'WorkshopItems-, Mods- und Map-Listen werden nach einer datierten Sicherung ersetzt. Andere Servereinstellungen bleiben unverändert.', 'As listas WorkshopItems, Mods e Map serão substituídas após um backup datado. As outras configurações permanecerão inalteradas.', '创建带时间戳的备份后，将替换 WorkshopItems、Mods 和 Map 列表，其他服务器设置保持不变。'],
        ['Une sauvegarde horodatée sera créée avant l’écriture atomique du fichier serveur.', 'A timestamped backup will be created before the server file is written atomically.', 'Se creará una copia con fecha antes de escribir atómicamente el archivo del servidor.', 'Vor dem atomaren Schreiben der Serverdatei wird eine datierte Sicherung erstellt.', 'Um backup datado será criado antes da gravação atômica do arquivo do servidor.', '原子写入服务器文件前将创建带时间戳的备份。'],
        ['Vue d’ensemble', 'Overview', 'Resumen', 'Übersicht', 'Visão geral', '概览'],
        ['Planification', 'Scheduling', 'Planificación', 'Zeitplanung', 'Agendamento', '计划任务'],
        ['Expert', 'Expert', 'Experto', 'Experte', 'Especialista', '专家'],
        ['Fenêtre à la connexion', 'Connection window', 'Ventana de conexión', 'Verbindungsfenster', 'Janela de conexão', '连接窗口'],
        ['Afficher la fenêtre PZASM', 'Show the PZASM window', 'Mostrar ventana PZASM', 'PZASM-Fenster anzeigen', 'Mostrar janela PZASM', '显示 PZASM 窗口'],
        ['Titre affiché', 'Displayed title', 'Título mostrado', 'Angezeigter Titel', 'Título exibido', '显示标题'],
        ['Configurations locales', 'Local configurations', 'Configuraciones locales', 'Lokale Konfigurationen', 'Configurações locais', '本地配置'],
        ['Une interface guidée pour les réglages courants, avec l’éditeur INI brut toujours disponible.', 'A guided interface for common settings, with the raw INI editor always available.', 'Una interfaz guiada para ajustes comunes, con el editor INI siempre disponible.', 'Eine geführte Oberfläche für übliche Einstellungen, der INI-Roheditor bleibt verfügbar.', 'Uma interface guiada para configurações comuns, com editor INI sempre disponível.', '常用设置采用引导式界面，并始终保留原始 INI 编辑器。'],
        ['SERVEURS PROJECT ZOMBOID', 'PROJECT ZOMBOID SERVERS', 'SERVIDORES PROJECT ZOMBOID', 'PROJECT-ZOMBOID-SERVER', 'SERVIDORES PROJECT ZOMBOID', 'PROJECT ZOMBOID 服务器'],
        ['profils', 'profiles', 'perfiles', 'Profile', 'perfis', '配置'],
        ['sauvegardé avant écriture', 'backed up before writing', 'copia antes de escribir', 'Sicherung vor dem Schreiben', 'backup antes de gravar', '写入前备份'],
        ['PROFILS DÉTECTÉS', 'DETECTED PROFILES', 'PERFILES DETECTADOS', 'ERKANNTE PROFILE', 'PERFIS DETECTADOS', '已检测配置'],
        ['Nouveau profil', 'New profile', 'Nuevo perfil', 'Neues Profil', 'Novo perfil', '新建配置'],
        ['Créer le profil', 'Create profile', 'Crear perfil', 'Profil erstellen', 'Criar perfil', '创建配置'],
        ['Centre de contrôle', 'Control center', 'Centro de control', 'Kontrollzentrum', 'Central de controle', '控制中心'],
        ['EN LIGNE', 'ONLINE', 'EN LÍNEA', 'ONLINE', 'ONLINE', '在线'],
        ['ARRÊTÉ / RCON INDISPONIBLE', 'STOPPED / RCON UNAVAILABLE', 'DETENIDO / RCON NO DISPONIBLE', 'GESTOPPT / RCON NICHT VERFÜGBAR', 'PARADO / RCON INDISPONÍVEL', '已停止 / RCON 不可用'],
        ['Démarrer', 'Start', 'Iniciar', 'Starten', 'Iniciar', '启动'],
        ['Lancer le serveur dédié', 'Launch the dedicated server', 'Iniciar el servidor dedicado', 'Dedicated Server starten', 'Iniciar o servidor dedicado', '启动专用服务器'],
        ['Sauvegarder et arrêter', 'Save and stop', 'Guardar y detener', 'Speichern und stoppen', 'Salvar e parar', '保存并停止'],
        ['save puis quit par RCON', 'save then quit via RCON', 'save y luego quit por RCON', 'save, dann quit per RCON', 'save e depois quit via RCON', '通过 RCON 先 save 后 quit'],
        ['APPLICATION SÛRE', 'SAFE APPLICATION', 'APLICACIÓN SEGURA', 'SICHERE ANWENDUNG', 'APLICAÇÃO SEGURA', '安全应用'],
        ['Installer un pack sur ce serveur', 'Install a pack on this server', 'Instalar un paquete en este servidor', 'Paket auf diesem Server installieren', 'Instalar um pacote neste servidor', '在此服务器安装模组包'],
        ['Seules les listes WorkshopItems, Mods et Map sont remplacées après sauvegarde.', 'Only WorkshopItems, Mods and Map lists are replaced after a backup.', 'Solo se reemplazan WorkshopItems, Mods y Map después de crear una copia.', 'Nur WorkshopItems, Mods und Map werden nach einer Sicherung ersetzt.', 'Somente WorkshopItems, Mods e Map são substituídos após o backup.', '备份后仅替换 WorkshopItems、Mods 与 Map 列表。'],
        ['Choisir un pack construit et publié…', 'Choose a built and published pack…', 'Elegir un paquete compilado y publicado…', 'Erstelltes und veröffentlichtes Paket wählen…', 'Escolher um pacote construído e publicado…', '选择已构建并发布的模组包…'],
        ['Appliquer le pack', 'Apply pack', 'Aplicar paquete', 'Paket anwenden', 'Aplicar pacote', '应用模组包'],
        ['Arrêtez proprement le serveur avant d’appliquer un pack.', 'Stop the server cleanly before applying a pack.', 'Detén correctamente el servidor antes de aplicar un paquete.', 'Server vor dem Anwenden eines Pakets sauber stoppen.', 'Pare o servidor corretamente antes de aplicar um pacote.', '应用模组包前请正常停止服务器。'],
        ['Identité', 'Identity', 'Identidad', 'Identität', 'Identidade', '身份'],
        ['Accès & RCON', 'Access & RCON', 'Acceso y RCON', 'Zugriff & RCON', 'Acesso e RCON', '访问与 RCON'],
        ['Session', 'Session', 'Sesión', 'Sitzung', 'Sessão', '会话'],
        ['Contenu', 'Content', 'Contenido', 'Inhalt', 'Conteúdo', '内容'],
        ['INI brut', 'Raw INI', 'INI sin procesar', 'INI-Rohdaten', 'INI bruto', '原始 INI'],
        ['CATALOGUE ET LISTES EXACTES', 'CATALOG AND EXACT LISTS', 'CATÁLOGO Y LISTAS EXACTAS', 'KATALOG UND EXAKTE LISTEN', 'CATÁLOGO E LISTAS EXATAS', '目录与精确列表'],
        ['Versions et configuration serveur', 'Versions and server configuration', 'Versiones y configuración del servidor', 'Versionen und Serverkonfiguration', 'Versões e configuração do servidor', '版本与服务器配置'],
        ['Le fichier serveur stocke uniquement les Workshop IDs et Mod IDs. Les versions déclarées, profils PZ et révisions figées restent visibles et vérifiables dans le projet du pack, son manifeste et la fenêtre en jeu.', 'The server file stores only Workshop IDs and Mod IDs. Declared versions, PZ profiles, and pinned revisions remain visible and verifiable in the pack project, its manifest, and the in-game window.', 'El archivo del servidor solo almacena Workshop IDs y Mod IDs. Las versiones declaradas, los perfiles PZ y las revisiones fijadas siguen visibles y verificables en el proyecto del paquete, su manifiesto y la ventana del juego.', 'Die Serverdatei speichert nur Workshop-IDs und Mod-IDs. Angegebene Versionen, PZ-Profile und fixierte Revisionen bleiben im Paketprojekt, Manifest und Spielfenster sichtbar und prüfbar.', 'O arquivo do servidor armazena apenas Workshop IDs e Mod IDs. Versões declaradas, perfis PZ e revisões fixadas continuam visíveis e verificáveis no projeto do pacote, no manifesto e na janela do jogo.', '服务器文件只保存 Workshop ID 和 Mod ID。声明版本、PZ 配置和固定修订仍可在模组包项目、清单和游戏内窗口中查看和核验。'],
        ['Workshop, mods et cartes', 'Workshop, mods and maps', 'Workshop, mods y mapas', 'Workshop, Mods und Karten', 'Workshop, mods e mapas', '创意工坊、模组与地图'],
        ['Composez le serveur visuellement depuis le Workshop ou les installations locales, puis ajustez les listes brutes si nécessaire.', 'Compose the server visually from the Workshop or local installations, then adjust raw lists when needed.', 'Configura visualmente el servidor desde Workshop o instalaciones locales y ajusta las listas si es necesario.', 'Server visuell aus Workshop oder lokalen Installationen zusammenstellen und Rohlisten bei Bedarf anpassen.', 'Monte o servidor visualmente pelo Workshop ou instalações locais e ajuste as listas quando necessário.', '从创意工坊或本地安装直观配置服务器，并在需要时调整原始列表。'],
        ['Mods installés', 'Installed mods', 'Mods instalados', 'Installierte Mods', 'Mods instalados', '已安装模组'],
        ['Sélection multiple et dépendances', 'Multi-selection and dependencies', 'Selección múltiple y dependencias', 'Mehrfachauswahl und Abhängigkeiten', 'Seleção múltipla e dependências', '多选与依赖'],
        ['L’ordre est significatif', 'Order matters', 'El orden es importante', 'Die Reihenfolge ist wichtig', 'A ordem é importante', '顺序很重要'],
        ['IDs numériques séparés par des points-virgules.', 'Numeric IDs separated by semicolons.', 'IDs numéricos separados por punto y coma.', 'Numerische IDs, durch Semikolon getrennt.', 'IDs numéricos separados por ponto e vírgula.', '数字 ID 以分号分隔。'],
        ['Mod IDs internes séparés par des points-virgules.', 'Internal Mod IDs separated by semicolons.', 'Mod IDs internos separados por punto y coma.', 'Interne Mod-IDs, durch Semikolon getrennt.', 'Mod IDs internos separados por ponto e vírgula.', '内部 Mod ID 以分号分隔。'],
        ['Dossiers de cartes dans l’ordre de priorité.', 'Map folders in priority order.', 'Carpetas de mapas por orden de prioridad.', 'Kartenordner in Prioritätsreihenfolge.', 'Pastas de mapas em ordem de prioridade.', '地图文件夹按优先级排序。'],
        ['Éditeur guidé', 'Guided editor', 'Editor guiado', 'Geführter Editor', 'Editor guiado', '引导式编辑器'],
        ['Une seule sauvegarde horodatée est créée pour l’ensemble des changements.', 'One timestamped backup is created for all changes.', 'Se crea una única copia con fecha para todos los cambios.', 'Eine datierte Sicherung wird für alle Änderungen erstellt.', 'Um único backup datado é criado para todas as alterações.', '所有更改只创建一个带时间戳的备份。'],
        ['Enregistrer les réglages', 'Save settings', 'Guardar ajustes', 'Einstellungen speichern', 'Salvar configurações', '保存设置'],
        ['VISIBILITÉ', 'VISIBILITY', 'VISIBILIDAD', 'SICHTBARKEIT', 'VISIBILIDADE', '可见性'],
        ['Identité du serveur', 'Server identity', 'Identidad del servidor', 'Serveridentität', 'Identidade do servidor', '服务器身份'],
        ['Nom public', 'Public name', 'Nombre público', 'Öffentlicher Name', 'Nome público', '公开名称'],
        ['Description publique', 'Public description', 'Descripción pública', 'Öffentliche Beschreibung', 'Descrição pública', '公开描述'],
        ['Serveur public', 'Public server', 'Servidor público', 'Öffentlicher Server', 'Servidor público', '公开服务器'],
        ['Connexions', 'Connections', 'Conexiones', 'Verbindungen', 'Conexões', '连接'],
        ['Serveur ouvert', 'Open server', 'Servidor abierto', 'Offener Server', 'Servidor aberto', '开放服务器'],
        ['RÉSEAU ET SÉCURITÉ', 'NETWORK AND SECURITY', 'RED Y SEGURIDAD', 'NETZWERK UND SICHERHEIT', 'REDE E SEGURANÇA', '网络与安全'],
        ['Accès et administration', 'Access and administration', 'Acceso y administración', 'Zugriff und Verwaltung', 'Acesso e administração', '访问与管理'],
        ['Joueurs', 'Players', 'Jugadores', 'Spieler', 'Jogadores', '玩家'],
        ['Mot de passe joueur', 'Player password', 'Contraseña de jugador', 'Spielerpasswort', 'Senha de jogador', '玩家密码'],
        ['Nombre maximal de joueurs', 'Maximum players', 'Máximo de jugadores', 'Maximale Spielerzahl', 'Máximo de jogadores', '最大玩家数'],
        ['Ports', 'Ports', 'Puertos', 'Ports', 'Portas', '端口'],
        ['Port du jeu', 'Game port', 'Puerto del juego', 'Spiel-Port', 'Porta do jogo', '游戏端口'],
        ['Port RCON', 'RCON port', 'Puerto RCON', 'RCON-Port', 'Porta RCON', 'RCON 端口'],
        ['Mot de passe RCON', 'RCON password', 'Contraseña RCON', 'RCON-Passwort', 'Senha RCON', 'RCON 密码'],
        ['Vérifier les checksums Lua', 'Verify Lua checksums', 'Verificar checksums Lua', 'Lua-Prüfsummen prüfen', 'Verificar checksums Lua', '验证 Lua 校验和'],
        ['Comportement multijoueur', 'Multiplayer behavior', 'Comportamiento multijugador', 'Mehrspielerverhalten', 'Comportamento multijogador', '多人游戏行为'],
        ['Monde actif', 'Active world', 'Mundo activo', 'Aktive Welt', 'Mundo ativo', '活动世界'],
        ['Pause quand le serveur est vide', 'Pause when server is empty', 'Pausar con servidor vacío', 'Pause bei leerem Server', 'Pausar com servidor vazio', '服务器无人时暂停'],
        ['PvP et sécurité', 'PvP and safety', 'PvP y seguridad', 'PvP und Sicherheit', 'PvP e segurança', 'PvP 与安全'],
        ['Sommeil', 'Sleep', 'Sueño', 'Schlaf', 'Sono', '睡眠'],
        ['Sauvegardes', 'Backups', 'Copias de seguridad', 'Sicherungen', 'Backups', '备份'],
        ['Import terminé', 'Import complete', 'Importación completada', 'Import abgeschlossen', 'Importação concluída', '导入完成'],
        ['Import interrompu', 'Import interrupted', 'Importación interrumpida', 'Import unterbrochen', 'Importação interrompida', '导入已中断'],
        ['Une intervention est nécessaire', 'Action is required', 'Se requiere intervención', 'Eingriff erforderlich', 'É necessária uma intervenção', '需要处理'],
        ['Finalisation', 'Finalizing', 'Finalizando', 'Abschluss', 'Finalizando', '正在完成'],
        ['En attente', 'Waiting', 'En espera', 'Wartend', 'Aguardando', '等待中'],
        ['Redirection vers votre configuration…', 'Redirecting to your configuration…', 'Redirigiendo a tu configuración…', 'Weiterleitung zu deiner Konfiguration…', 'Redirecionando para sua configuração…', '正在跳转到配置…'],
        ['Téléchargement SteamCMD…', 'SteamCMD download…', 'Descarga de SteamCMD…', 'SteamCMD-Download…', 'Download pelo SteamCMD…', 'SteamCMD 下载中…'],
        ['Téléchargement SteamCMD et vérification des fichiers…', 'SteamCMD download and file verification…', 'Descarga de SteamCMD y verificación de archivos…', 'SteamCMD-Download und Dateiprüfung…', 'Download pelo SteamCMD e verificação de arquivos…', 'SteamCMD 下载并验证文件…'],
        ['Lecture des mod.info, versions et dépendances…', 'Reading mod.info files, versions and dependencies…', 'Leyendo mod.info, versiones y dependencias…', 'mod.info, Versionen und Abhängigkeiten werden gelesen…', 'Lendo mod.info, versões e dependências…', '正在读取 mod.info、版本与依赖…'],
        ['Votre sélection est conservée entre les pages et les recherches.', 'Your selection is preserved across pages and searches.', 'Tu selección se conserva entre páginas y búsquedas.', 'Deine Auswahl bleibt über Seiten und Suchen hinweg erhalten.', 'Sua seleção é mantida entre páginas e pesquisas.', '选择会在翻页和搜索时保留。'],
        ['Votre sélection est conservée entre les pages, recherches et tris.', 'Your selection is preserved across pages, searches and sorting.', 'Tu selección se conserva entre páginas, búsquedas y ordenaciones.', 'Deine Auswahl bleibt über Seiten, Suchen und Sortierungen hinweg erhalten.', 'Sua seleção é mantida entre páginas, pesquisas e ordenações.', '选择会在翻页、搜索和排序时保留。'],
        ['Chaque item sera téléchargé anonymement puis inspecté avant modification.', 'Each item is downloaded anonymously and inspected before any change.', 'Cada elemento se descarga de forma anónima y se inspecciona antes de modificarlo.', 'Jedes Item wird anonym geladen und vor Änderungen geprüft.', 'Cada item é baixado anonimamente e inspecionado antes de qualquer alteração.', '每个项目都会匿名下载并在修改前检查。'],
        ['Les dépendances trouvées localement seront incluses automatiquement.', 'Locally found dependencies are included automatically.', 'Las dependencias locales se incluyen automáticamente.', 'Lokal gefundene Abhängigkeiten werden automatisch einbezogen.', 'As dependências locais são incluídas automaticamente.', '本地发现的依赖会自动包含。'],
        ['© 2026 LemonCorp · PZ Advanced Server Manager', '© 2026 LemonCorp · PZ Advanced Server Manager', '© 2026 LemonCorp · PZ Advanced Server Manager', '© 2026 LemonCorp · PZ Advanced Server Manager', '© 2026 LemonCorp · PZ Advanced Server Manager', '© 2026 LemonCorp · PZ Advanced Server Manager']
        ,['Outil indépendant — Project Zomboid et Steam appartiennent à leurs détenteurs respectifs.', 'Independent tool — Project Zomboid and Steam belong to their respective owners.', 'Herramienta independiente: Project Zomboid y Steam pertenecen a sus respectivos propietarios.', 'Unabhängiges Werkzeug — Project Zomboid und Steam gehören ihren jeweiligen Rechteinhabern.', 'Ferramenta independente — Project Zomboid e Steam pertencem aos seus respectivos proprietários.', '独立工具——Project Zomboid 与 Steam 的权利归各自所有者。']
    ];

    const dictionaries = Object.fromEntries(languages.map((language, index) => [language, new Map(rows.map(row => [row[0], row[index]]))]));
    const textOriginals = new WeakMap();
    const attributeOriginals = new WeakMap();
    const languageSelect = document.querySelector('[data-language-select]');
    const supported = language => languages.includes(language) ? language : languages.find(value => language?.toLowerCase().startsWith(value.split('-')[0].toLowerCase()));
    let activeLanguage = supported(localStorage.getItem('pzasm-language')) || supported(navigator.language) || 'fr';

    const translatePattern = value => {
        const dictionary = dictionaries[activeLanguage];
        if (dictionary.has(value)) return dictionary.get(value);
        if (activeLanguage === 'fr') return value;
        const patterns = [
            [/^(\d+) mod\(s\) sélectionné\(s\)$/, ['$1 mod(s) selected', '$1 mod(s) seleccionado(s)', '$1 Mod(s) ausgewählt', '$1 mod(s) selecionado(s)', '已选择 $1 个模组']],
            [/^Page (\d+)$/, ['Page $1', 'Página $1', 'Seite $1', 'Página $1', '第 $1 页']],
            [/^(\d+) résultats · page (\d+)$/, ['$1 results · page $2', '$1 resultados · página $2', '$1 Ergebnisse · Seite $2', '$1 resultados · página $2', '$1 个结果 · 第 $2 页']],
            [/^Version (.+)$/, ['Version $1', 'Versión $1', 'Version $1', 'Versão $1', '版本 $1']],
            [/^Profil PZ (.+)$/, ['PZ profile $1', 'Perfil PZ $1', 'PZ-Profil $1', 'Perfil PZ $1', 'PZ 配置 $1']],
            [/^Révision (.+)$/, ['Revision $1', 'Revisión $1', 'Revision $1', 'Revisão $1', '修订版 $1']],
            [/^Workshop (\d+)$/, ['Workshop $1', 'Workshop $1', 'Workshop $1', 'Workshop $1', '创意工坊 $1']]
            ,[/^← Retour à (.+)$/, ['← Back to $1', '← Volver a $1', '← Zurück zu $1', '← Voltar para $1', '← 返回 $1']]
            ,[/^← Tableau de bord$/, ['← Dashboard', '← Panel principal', '← Übersicht', '← Painel', '← 控制面板']]
            ,[/^(.+) — Workshop non publié$/, ['$1 — Workshop unpublished', '$1 — Workshop no publicado', '$1 — Workshop nicht veröffentlicht', '$1 — Workshop não publicado', '$1 — 创意工坊未发布']]
            ,[/^(\d+) mods compatibles · page (\d+)$/, ['$1 compatible mods · page $2', '$1 mods compatibles · página $2', '$1 kompatible Mods · Seite $2', '$1 mods compatíveis · página $2', '$1 个兼容模组 · 第 $2 页']]
            ,[/^(\d+) dépendance\(s\) résolue\(s\) automatiquement$/, ['$1 dependency/dependencies resolved automatically', '$1 dependencia(s) resuelta(s) automáticamente', '$1 Abhängigkeit(en) automatisch aufgelöst', '$1 dependência(s) resolvida(s) automaticamente', '已自动解析 $1 个依赖']]
            ,[/^Workshop (\d+) · profil PZ (.+)$/, ['Workshop $1 · PZ profile $2', 'Workshop $1 · perfil PZ $2', 'Workshop $1 · PZ-Profil $2', 'Workshop $1 · perfil PZ $2', '创意工坊 $1 · PZ 配置 $2']]
            ,[/^Workshop: à créer$/, ['Workshop: to create', 'Workshop: por crear', 'Workshop: zu erstellen', 'Workshop: a criar', '创意工坊：待创建']]
            ,[/^(\d+)\/(\d+) mods suivis par la mise à jour globale$/, ['$1/$2 mods included in global updates', '$1/$2 mods incluidos en la actualización global', '$1/$2 Mods in globalen Aktualisierungen', '$1/$2 mods incluídos na atualização global', '$1/$2 个模组包含在全局更新中']]
            ,[/^(\d+) sélectionné\(s\), (\d+) exclu\(s\)$/, ['$1 selected, $2 excluded', '$1 seleccionados, $2 excluidos', '$1 ausgewählt, $2 ausgeschlossen', '$1 selecionados, $2 excluídos', '已选择 $1，已排除 $2']]
            ,[/^Prérempli depuis mod\.info : (.+)$/, ['Prefilled from mod.info: $1', 'Rellenado desde mod.info: $1', 'Aus mod.info vorausgefüllt: $1', 'Preenchido pelo mod.info: $1', '已从 mod.info 预填：$1']]
        ];
        const languageIndex = Math.max(0, languages.indexOf(activeLanguage) - 1);
        for (const [pattern, values] of patterns) {
            if (pattern.test(value)) return value.replace(pattern, values[languageIndex] || values[0]);
        }
        return value;
    };

    const translateTextNode = (node, externalMutation = false) => {
        if (!node.parentElement || ['SCRIPT', 'STYLE', 'TEXTAREA', 'CODE'].includes(node.parentElement.tagName)) return;
        let original = textOriginals.get(node);
        if (original === undefined) {
            original = node.textContent;
            textOriginals.set(node, original);
        } else if (externalMutation) {
            const expected = translatePattern(original.trim());
            if (node.textContent.trim() !== expected) {
                original = node.textContent;
                textOriginals.set(node, original);
            }
        }
        const leading = original.match(/^\s*/)?.[0] || '';
        const trailing = original.match(/\s*$/)?.[0] || '';
        const core = original.trim();
        if (core) {
            const translated = leading + translatePattern(core) + trailing;
            if (node.textContent !== translated) node.textContent = translated;
        }
    };

    const translateAttributes = element => {
        const names = ['placeholder', 'title', 'aria-label', 'data-loading-title', 'data-loading-detail', 'data-confirm-title', 'data-confirm-message', 'data-confirm-action'];
        let originals = attributeOriginals.get(element);
        if (!originals) { originals = new Map(); attributeOriginals.set(element, originals); }
        names.forEach(name => {
            if (!element.hasAttribute(name)) return;
            if (!originals.has(name)) originals.set(name, element.getAttribute(name));
            element.setAttribute(name, translatePattern(originals.get(name)));
        });
    };

    const translateTree = (root, externalMutation = false) => {
        if (root.nodeType === Node.TEXT_NODE) { translateTextNode(root, externalMutation); return; }
        if (!(root instanceof Element) && root !== document) return;
        if (root instanceof Element) translateAttributes(root);
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
        while (walker.nextNode()) {
            if (walker.currentNode.nodeType === Node.TEXT_NODE) translateTextNode(walker.currentNode, externalMutation);
            else translateAttributes(walker.currentNode);
        }
    };

    const applyLanguage = language => {
        activeLanguage = supported(language) || 'fr';
        document.documentElement.lang = activeLanguage;
        if (languageSelect) languageSelect.value = activeLanguage;
        localStorage.setItem('pzasm-language', activeLanguage);
        translateTree(document);
        window.dispatchEvent(new CustomEvent('pzasm:language', { detail: activeLanguage }));
    };

    languageSelect?.addEventListener('change', event => applyLanguage(event.target.value));
    new MutationObserver(mutations => mutations.forEach(mutation => {
        if (mutation.type === 'characterData') translateTextNode(mutation.target, true);
        mutation.addedNodes.forEach(node => translateTree(node, true));
    })).observe(document.body, { childList: true, subtree: true, characterData: true });
    applyLanguage(activeLanguage);
})();
