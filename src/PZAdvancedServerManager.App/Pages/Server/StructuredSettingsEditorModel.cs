using PZAdvancedServerManager.Core.Pz;

namespace PZAdvancedServerManager.App.Pages.Server;

public sealed record StructuredSettingsEditorModel(
    IReadOnlyList<StructuredServerSetting> Settings,
    string CatalogId,
    string SearchPlaceholder,
    string EmptyMessage);
