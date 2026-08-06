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
        var legal = LuaString("Ce pack a été assemblé avec PZ Advanced Server Manager. Les auteurs de chaque mod restent propriétaires de leur travail. Le créateur du pack est responsable d'avoir obtenu les autorisations nécessaires.");
        var modLines = project.Mods.Where(x => x.Enabled).OrderBy(x => x.Order).ThenBy(x => x.Name).Select((x, i) => $"{i + 1}. {x.Name} — Mod ID: {x.ModId} — Workshop: {(x.WorkshopId == 0 ? "local" : x.WorkshopId)} — auteur: {(string.IsNullOrWhiteSpace(x.Author) ? "non renseigné" : x.Author)}");
        var exhaustiveList = LuaString(string.Join("\n", modLines));

        var lua = $$"""
require "ISUI/ISPanel"
require "ISUI/ISRichTextPanel"
require "ISUI/ISButton"

local PZASM_NOTICE_SHOWN = false

local function pzasmEscapeRichText(value)
    return string.gsub(string.gsub(value or "", "<", "&lt;"), ">", "&gt;")
end

local function pzasmShowPackNotice()
    if PZASM_NOTICE_SHOWN then return end
    PZASM_NOTICE_SHOWN = true

    local width = math.min(760, getCore():getScreenWidth() - 80)
    local height = math.min(620, getCore():getScreenHeight() - 80)
    local panel = ISPanel:new((getCore():getScreenWidth() - width) / 2, (getCore():getScreenHeight() - height) / 2, width, height)
    panel:initialise()
    panel:addToUIManager()
    panel.backgroundColor = {r=0.045, g=0.055, b=0.050, a=0.98}
    panel.borderColor = {r=0.55, g=0.78, b=0.35, a=1}
    panel.moveWithMouse = true

    local rich = ISRichTextPanel:new(22, 18, width - 44, height - 82)
    rich:initialise()
    rich:addScrollBars()
    rich.background = false
    rich.clip = true
    rich.text = "<H1>" .. pzasmEscapeRichText({{title}}) .. "</H1><LINE>" ..
        "<H2>" .. pzasmEscapeRichText({{packName}}) .. "</H2><LINE>" ..
        pzasmEscapeRichText({{description}}) .. "<LINE><LINE>" ..
        "<RGB:0.84,0.67,0.25>INFORMATION SUR LES DROITS<RGB:1,1,1><LINE>" .. pzasmEscapeRichText({{legal}}) ..
        "<LINE><LINE><H2>Mods inclus</H2><LINE>" .. string.gsub(pzasmEscapeRichText({{exhaustiveList}}), "\n", "<LINE>")
    rich:paginate()
    panel:addChild(rich)

    local close = ISButton:new(width - 142, height - 52, 120, 32, "J'ai compris", panel, function(target)
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
