using System.Text;
using PZAdvancedServerManager.Core.Domain;

namespace PZAdvancedServerManager.Core.Packaging;

public static class NoticeModGenerator
{
    public static void GenerateStandalone(string modsRoot, PackageProject project)
    {
        var root = Path.Combine(modsRoot, project.NoticeModId);
        WriteManifest(root, project.NoticeModId, $"{project.Name} — notice", "Fenêtre d'information générée par PZ Advanced Server Manager.");
        WriteLua(Path.Combine(root, "common", "media", "lua", "client", "PZASM_PackNotice.lua"), project);
    }

    public static void InjectIntoFusion(string fusionRoot, PackageProject project)
    {
        WriteLua(Path.Combine(fusionRoot, "common", "media", "lua", "client", "PZASM_PackNotice.lua"), project);
    }

    public static void WriteManifest(string root, string modId, string name, string description)
    {
        Directory.CreateDirectory(root);
        var manifest = $"name={SanitizeLine(name)}\nid={SanitizeLine(modId)}\ndescription={SanitizeLine(description)}\nauthor=LemonCorp / pack creator\npzversion=42\nversionMin=42.0\n";
        File.WriteAllText(Path.Combine(root, "mod.info"), manifest, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(root, "common"));
        File.WriteAllText(Path.Combine(root, "common", "mod.info"), manifest, new UTF8Encoding(false));
    }

    private static void WriteLua(string path, PackageProject project)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var title = LuaString(project.NoticeTitle);
        var packName = LuaString(project.Name);
        var description = LuaString(project.Description);
        var legalFr = LuaString("Ce pack a été assemblé avec PZ Advanced Server Manager. Les auteurs de chaque mod restent propriétaires de leur travail. Le créateur du pack est responsable d'avoir obtenu les autorisations nécessaires.");
        var legalEn = LuaString("This pack was assembled with PZ Advanced Server Manager. Each mod remains the property of its author. The pack creator is responsible for obtaining the required permissions.");
        var legalEs = LuaString("Este paquete fue creado con PZ Advanced Server Manager. Cada mod sigue siendo propiedad de su autor. El creador del paquete es responsable de obtener los permisos necesarios.");
        var legalDe = LuaString("Dieses Paket wurde mit PZ Advanced Server Manager erstellt. Jeder Mod bleibt Eigentum seines Autors. Der Paketersteller ist für die erforderlichen Genehmigungen verantwortlich.");
        var legalPt = LuaString("Este pacote foi criado com o PZ Advanced Server Manager. Cada mod continua sendo propriedade do autor. O criador do pacote é responsável pelas permissões necessárias.");
        var legalZh = LuaString("此模组包由 PZ Advanced Server Manager 组装。每个模组的权利仍归其作者所有，模组包创建者负责取得所需许可。");
        var modLines = project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).ThenBy(x => x.Name).Select((x, i) =>
            $"{i + 1}. {x.Name} — Version: {x.DisplayVersion} — PZ: {(string.IsNullOrWhiteSpace(x.SelectedVersionFolder) ? "root" : x.SelectedVersionFolder)} — Revision: {(string.IsNullOrWhiteSpace(x.PinnedContentHash) ? "not pinned" : x.PinnedContentHash[..Math.Min(12, x.PinnedContentHash.Length)])} — Mod ID: {x.ModId} — Workshop: {(x.WorkshopId == 0 ? "local" : x.WorkshopId)} — Author: {(string.IsNullOrWhiteSpace(x.Author) ? "—" : x.Author)}");
        var exhaustiveList = LuaString(string.Join("\n", modLines));
        var modCount = project.Mods.Count(x => x.Enabled);
        var controlEnabled = project.InjectInGameControl ? "true" : "false";

        var lua = $$"""
require "ISUI/ISPanel"
require "ISUI/ISRichTextPanel"
require "ISUI/ISButton"

local PZASM_NOTICE_SHOWN = false
local PZASM_CONTROL_ENABLED = {{controlEnabled}}

local PZASM_TEXT = {
    fr = { rights = "INFORMATION SUR LES DROITS", mods = "Mods inclus et versions", close = "J'ai compris", legal = {{legalFr}} },
    en = { rights = "RIGHTS INFORMATION", mods = "Included mods and versions", close = "I understand", legal = {{legalEn}} },
    es = { rights = "INFORMACIÓN DE DERECHOS", mods = "Mods y versiones incluidos", close = "Entendido", legal = {{legalEs}} },
    de = { rights = "RECHTEINFORMATIONEN", mods = "Enthaltene Mods und Versionen", close = "Verstanden", legal = {{legalDe}} },
    pt = { rights = "INFORMAÇÕES DE DIREITOS", mods = "Mods e versões incluídos", close = "Entendi", legal = {{legalPt}} },
    zh = { rights = "权利信息", mods = "包含的模组与版本", close = "我已了解", legal = {{legalZh}} }
}

local function pzasmLanguage()
    local value = "en"
    local ok, detected = pcall(function()
        if Translator and Translator.getLanguage then return tostring(Translator.getLanguage():name()) end
        if getCore and getCore().getOptionLanguageName then return tostring(getCore():getOptionLanguageName()) end
        return "en"
    end)
    if ok and detected then value = string.lower(detected) end
    if string.find(value, "fr") or string.find(value, "french") then return "fr" end
    if string.find(value, "es") or string.find(value, "spanish") then return "es" end
    if string.find(value, "de") or string.find(value, "german") then return "de" end
    if string.find(value, "pt") or string.find(value, "portugu") then return "pt" end
    if string.find(value, "zh") or string.find(value, "cn") or string.find(value, "chinese") then return "zh" end
    return "en"
end

local function pzasmEscapeRichText(value)
    return string.gsub(string.gsub(value or "", "<", "&lt;"), ">", "&gt;")
end

local function pzasmShowPackNotice()
    if PZASM_NOTICE_SHOWN then return end
    PZASM_NOTICE_SHOWN = true
    local text = PZASM_TEXT[pzasmLanguage()] or PZASM_TEXT.en

    local width = math.min(760, getCore():getScreenWidth() - 80)
    local height = math.min(620, getCore():getScreenHeight() - 80)
    local panel = ISPanel:new((getCore():getScreenWidth() - width) / 2, (getCore():getScreenHeight() - height) / 2, width, height)
    panel:initialise()
    panel:addToUIManager()
    panel.backgroundColor = {r=0.045, g=0.055, b=0.050, a=0.98}
    panel.borderColor = {r=0.55, g=0.78, b=0.35, a=1}
    panel.moveWithMouse = true
    panel.prerender = function(self)
        ISPanel.prerender(self)
        self:drawRect(0, 0, self.width, 52, 0.98, 0.08, 0.14, 0.09)
        self:drawRect(0, 51, self.width, 2, 1, 0.55, 0.78, 0.35)
        self:drawText("PZ", 20, 15, 0.66, 0.86, 0.38, 1, UIFont.Medium)
        self:drawText("ADVANCED SERVER MANAGER", 58, 16, 0.92, 0.95, 0.90, 1, UIFont.Small)
        self:drawTextRight("{{modCount}} MANAGED MODS", self.width - 20, 17, 0.66, 0.86, 0.38, 1, UIFont.Small)
    end

    local rich = ISRichTextPanel:new(22, 70, width - 44, height - 146)
    rich.autosetheight = false
    rich.marginLeft = 8
    rich.marginRight = 18
    rich.marginTop = 8
    rich.marginBottom = 12
    rich:initialise()
    rich:addScrollBars()
    rich:ignoreHeightChange()
    rich.vscroll:setVisible(true)
    rich.vscroll.background = false
    rich.background = false
    rich.clip = true
    rich.text = "<H1>" .. pzasmEscapeRichText({{title}}) .. "</H1><LINE>" ..
        "<RGB:0.55,0.82,0.34><B>" .. pzasmEscapeRichText({{packName}}) .. "</B><RGB:1,1,1><LINE>" ..
        pzasmEscapeRichText({{description}}) .. "<LINE><LINE>" ..
        "<RGB:0.84,0.67,0.25>" .. text.rights .. "<RGB:1,1,1><LINE>" .. pzasmEscapeRichText(text.legal) ..
        "<LINE><LINE><H2>" .. text.mods .. "</H2><LINE>" .. string.gsub(pzasmEscapeRichText({{exhaustiveList}}), "\n", "<LINE>")
    rich:paginate()
    rich:setYScroll(0)
    panel:addChild(rich)

    if PZASM_CONTROL_ENABLED then
        local control = ISButton:new(22, height - 55, 230, 34, "PZASM Control · F8", panel, function(target)
            if _G.PZASM_OpenControl then _G.PZASM_OpenControl() end
            target:setVisible(false)
        end)
        control:initialise()
        control:instantiate()
        panel:addChild(control)
    end

    local close = ISButton:new(width - 164, height - 55, 142, 34, text.close, panel, function(target)
        target:setVisible(false)
        target:removeFromUIManager()
    end)
    close:initialise()
    close:instantiate()
    panel:addChild(close)
end

Events.OnConnected.Add(pzasmShowPackNotice)
""";
        File.WriteAllText(path, lua, new UTF8Encoding(false));
    }

    private static string LuaString(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static string SanitizeLine(string value) => value.Replace("\r", " ").Replace("\n", " ").Trim();
}
