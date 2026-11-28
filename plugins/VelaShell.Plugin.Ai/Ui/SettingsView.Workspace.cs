using Avalonia.Controls;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

public partial class SettingsView
{
    internal void EnableProviderSetup()
    {
        ProviderCatalogRequested += ShowSetup;
        if (!_settings.Providers.Any(p => p.Auth == AuthMethod.ApiKey)) ShowSetup(null);

        void ShowSetup(string? id)
        {
            if (id is not null || !SettingsLayout.IsVisible) return;
            var setup = new ProviderSetupView(_context, _store, _settings, _loc, health: _health);
            SettingsLayout.IsVisible = false;
            WorkspacePages.Children.Add(setup);
            setup.FocusNavigation();
            setup.BackRequested += Return;
            setup.ProviderChanged += changedId =>
            {
                if (TopLevel.GetTopLevel(this) is not null)
                {
                    if (WorkspacePages.Children.Contains(setup)) ReloadFromCatalog(changedId);
                    else RefreshCatalogModels();
                }
                _onProvidersChanged();
            };
            setup.ModelsChanged += () =>
            {
                if (TopLevel.GetTopLevel(this) is not null) RefreshCatalogModels();
                ModelsChanged?.Invoke();
            };
            setup.Created += Return;

            void Return()
            {
                if (!WorkspacePages.Children.Remove(setup)) return;
                SettingsLayout.IsVisible = true;
                ProvidersList.Focus();
            }
        }
    }
}
