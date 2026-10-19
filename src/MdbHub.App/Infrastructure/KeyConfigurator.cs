using System.Windows;
using MdbHub.App.ViewModels;
using MdbHub.App.Views;
using MdbHub.Core;

namespace MdbHub.App.Infrastructure;

public static class KeyConfigurator
{
    /// <summary>
    /// Asks the user for the key of every table that has neither a config rule nor a declared primary key, and saves the answers.
    /// Tables left unanswered stay unconfigured (and are skipped by the caller). Returns true when the config changed.
    /// </summary>
    public static bool PromptForMissing(ConfigService config, params ResolvedSchema[] schemas)
    {
        // A table that already has a (broken) rule is fixed in the settings tab, not here.
        var missing = schemas
            .SelectMany(s => s.NeedingKey)
            .Where(t => !config.Config.Tables.ContainsKey(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (missing.Count == 0) return false;

        var suggested = schemas
            .SelectMany(s => s.Schema.Tables)
            .SelectMany(t => t.PrimaryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var viewModel = new KeySetupViewModel(missing, suggested);
        var window = new KeySetupWindow { DataContext = viewModel, Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return false;

        var rules = viewModel.ToRules();
        if (rules.Count == 0) return false;

        config.SaveRules(rules);
        return true;
    }
}
