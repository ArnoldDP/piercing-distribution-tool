using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mutagen.Bethesda.Plugins;
using PiercingDistributionTool.Core.Assets;
using PiercingDistributionTool.Core.Game;
using PiercingDistributionTool.Core.Patching;
using PiercingDistributionTool.Core.Preview;
using PiercingDistributionTool.Core.Rules;

namespace PiercingDistributionTool;

public partial class MainWindow : Window
{
    private readonly ToolSettings _settings = ToolSettings.Load();
    private GameSession? _session;
    private NpcModelBuilder? _builder;
    private TextureLoader? _textures;
    private IReadOnlyList<NpcEntry> _npcs = Array.Empty<NpcEntry>();
    private Dictionary<FormKey, PiercingSet> _assigned = new();
    private List<RecordRow> _records = new();
    private Dictionary<FormKey, RecordRow> _addonsByKey = new();
    private bool _syncing;

    private const string NoSet = "(none)";

    public MainWindow()
    {
        InitializeComponent();
        _syncing = true;
        DataFolderBox.Text = _settings.DataFolder ?? "";
        OutputNameBox.Text = _settings.OutputPluginName;
        OutputFolderBox.Text = _settings.OutputFolder ?? "";
        MergeBox.IsChecked = _settings.MergeIntoBody;
        StripReflectionsBox.IsChecked = _settings.StripReflections;
        _syncing = false;
        RefreshSetList();
        Closing += (_, _) => { SaveSettings(); _session?.Dispose(); };
    }

    // ---------- loading ----------

    private async void OnLoadClick(object sender, RoutedEventArgs e)
    {
        LoadButton.IsEnabled = false;
        Status("Reading your load order…");
        var folder = string.IsNullOrWhiteSpace(DataFolderBox.Text) ? null : DataFolderBox.Text.Trim();
        var ownPatch = ModKey.FromFileName(_settings.OutputPluginName);
        try
        {
            var (session, npcs, records) = await Task.Run(() =>
            {
                var s = GameSession.Open(folder, ownPatch);
                return (s, NpcEntry.Collect(s), RecordRow.Collect(s));
            });
            _session?.Dispose();
            _session = session;
            _npcs = npcs;
            _records = records;
            _addonsByKey = records.Where(r => r.IsAddon).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
            var assets = new AssetResolver(session.DataFolder);
            _builder = new NpcModelBuilder(session, assets);
            _textures = new TextureLoader(assets);
            _settings.DataFolder = folder;
            WriteButton.IsEnabled = true;
            Recompute();
            RefreshSetAddons();
            RefreshPluginList();
            OnRecordSearchChanged(this, e);
            Status($"Loaded {session.LoadOrder.Count} plugins and {npcs.Count} NPCs from {session.DataFolder}.");
        }
        catch (Exception ex)
        {
            Status("Could not load the game: " + ex.Message);
        }
        finally
        {
            LoadButton.IsEnabled = true;
        }
    }

    private void OnBrowseDataClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose Skyrim Special Edition's Data folder" };
        if (dlg.ShowDialog(this) == true) DataFolderBox.Text = dlg.FolderName;
    }

    // ---------- NPC list ----------

    private void Recompute()
    {
        _assigned = Distributor.Assign(_npcs, _settings).ToDictionary(a => a.Npc.FormKey, a => a.Set);
        RefreshNpcList();
    }

    private void RefreshNpcList()
    {
        var query = NpcSearch.Text.Trim();
        var gender = GenderFilterBox.SelectedIndex;
        var assignedOnly = AssignedOnlyBox.IsChecked == true;
        var rows = _npcs
            .Where(n => gender <= 0 || (gender == 1) == n.IsFemale)
            .Where(n => !assignedOnly || _assigned.ContainsKey(n.FormKey))
            .Where(n => query.Length == 0
                        || n.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || n.RaceEditorId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Display)
            .Select(n => new NpcRow(n, _assigned.TryGetValue(n.FormKey, out var s) ? s.Name : null))
            .ToList();
        var selected = (NpcList.SelectedItem as NpcRow)?.Npc.FormKey;
        _syncing = true;
        NpcList.ItemsSource = rows;
        if (selected != null) NpcList.SelectedItem = rows.FirstOrDefault(r => r.Npc.FormKey == selected);
        _syncing = false;
        NpcCountText.Text = $"{rows.Count} shown · {_assigned.Count} with piercings";
    }

    private void OnNpcFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshNpcList();
    }

    private void OnNpcSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) SyncNpcPanel(rebuildPreview: true);
    }

    private void SyncNpcPanel(bool rebuildPreview)
    {
        var row = NpcList.SelectedItem as NpcRow;
        _syncing = true;
        var names = new List<string> { NoSet };
        names.AddRange(_settings.Sets.Select(s => s.Name));
        NpcSetBox.ItemsSource = names;
        NpcSetBox.SelectedItem = row?.SetName ?? NoSet;
        NpcSetBox.IsEnabled = row != null && row.Npc.SkipReason == null;
        SelectedNpcText.Text = row == null ? "Pick an NPC on the left."
            : row.Npc.SkipReason != null ? $"{row.Npc.Display}: can't be given piercings ({row.Npc.SkipReason})."
            : $"{row.Npc.Display} [{row.Npc.FormKey}]";
        var previewed = PreviewSetBox.SelectedItem as string;
        PreviewSetBox.ItemsSource = names;
        PreviewSetBox.SelectedItem = rebuildPreview || previewed == null || !names.Contains(previewed) ? row?.SetName ?? NoSet : previewed;
        _syncing = false;
        if (rebuildPreview) RebuildPreview();
    }

    private void OnNpcSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || NpcList.SelectedItem is not NpcRow row || NpcSetBox.SelectedItem is not string choice) return;
        var key = row.Npc.FormKey.ToString();
        if (choice == NoSet) _settings.Rules.Overrides.Remove(key);
        else _settings.Rules.Overrides[key] = choice;
        SaveSettings();
        Recompute();
        SyncNpcPanel(rebuildPreview: true);
    }

    // ---------- preview ----------

    private void OnPreviewSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) RebuildPreview();
    }

    private void OnShowPiercingsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Preview.ShowPiercings = ShowPiercingsBox.IsChecked == true;
        Preview.Refresh();
    }

    private void RebuildPreview()
    {
        if (_builder == null || _textures == null || NpcList.SelectedItem is not NpcRow row)
        {
            if (_textures != null) Preview.SetModel(null, _textures);
            return;
        }
        var setName = PreviewSetBox.SelectedItem as string;
        var set = _settings.Sets.FirstOrDefault(s => s.Name == setName);
        try
        {
            var model = _builder.Build(row.Npc, set);
            Preview.SetModel(model, _textures);
            var info = $"{row.Npc.Display} · {row.Npc.RaceEditorId} · skin {row.Npc.SkinKey}"
                       + (row.Npc.SkinFromRace ? " (from race)" : " (WNAM)")
                       + $" · winning record from {row.Npc.WinningPlugin}";
            if (row.Npc.SkipReason != null) info += $"\nSkipped: {row.Npc.SkipReason}.";
            if (set != null && _session != null && !_settings.MergeIntoBody)
                foreach (var warning in SlotCheck.Conflicts(_session.LinkCache, row.Npc, set))
                    info += "\n⚠ " + warning;
            if (model.MissingPiercings.Count > 0)
                info += $"\n{model.MissingPiercings.Count} piercing mesh(es) don't exist, so they won't show here or in game. "
                        + "If the piercing mod has a BodySlide project, build it in BodySlide first: "
                        + string.Join(", ", model.MissingPiercings.Take(3));
            if (model.Missing.Count > 0) info += "\nNot found: " + string.Join(", ", model.Missing.Take(4));
            var unreadable = model.Parts.Where(p => p.DiffusePath != null && _textures.Load(p.DiffusePath) == null)
                .Select(p => $"{p.Name} ({p.DiffusePath})").Distinct().Take(4).ToList();
            if (unreadable.Count > 0) info += "\nTextures that couldn't be read (drawn grey): " + string.Join(", ", unreadable);
            PreviewInfo.Text = info;
        }
        catch (Exception ex)
        {
            PreviewInfo.Text = "Preview failed: " + ex.Message;
        }
    }

    // ---------- sets ----------

    private void RefreshSetList(PiercingSet? select = null)
    {
        SetList.ItemsSource = null;
        SetList.ItemsSource = _settings.Sets;
        if (select != null) SetList.SelectedItem = select;
        SyncNpcPanel(rebuildPreview: false);
    }

    private PiercingSet? SelectedSet => SetList.SelectedItem as PiercingSet;

    private void OnAddSet(object sender, RoutedEventArgs e) => CreateSet("New set");

    private PiercingSet CreateSet(string name)
    {
        var set = new PiercingSet { Name = UniqueSetName(name) };
        _settings.Sets.Add(set);
        RefreshSetList(set);
        return set;
    }

    private void OnRemoveSet(object sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { } set) return;
        var users = _settings.Rules.Overrides.Where(kv => kv.Value == set.Name).Select(kv => kv.Key).ToList();
        if (users.Count > 0 && MessageBox.Show(this, $"{users.Count} NPC(s) use \"{set.Name}\". Delete it and take it off them?",
                "Delete set", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var key in users) _settings.Rules.Overrides.Remove(key);
        _settings.Sets.Remove(set);
        RefreshSetList();
        Recompute();
        SyncNpcPanel(rebuildPreview: true);
    }

    private void OnSetSelected(object sender, SelectionChangedEventArgs e)
    {
        var set = SelectedSet;
        SetEditor.IsEnabled = set != null;
        _syncing = true;
        SetNameBox.Text = set?.Name ?? "";
        _syncing = false;
        RefreshSetAddons();
    }

    private void OnSetNameChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || SelectedSet is not { } set) return;
        var newName = SetNameBox.Text.Trim();
        if (newName.Length == 0 || newName == set.Name) return;
        newName = UniqueSetName(newName, set);
        foreach (var key in _settings.Rules.Overrides.Where(kv => kv.Value == set.Name).Select(kv => kv.Key).ToList())
            _settings.Rules.Overrides[key] = newName;
        set.Name = newName;
        RefreshSetList(set);
        Recompute();
        SyncNpcPanel(rebuildPreview: false);
    }

    private void RefreshSetAddons()
    {
        SetAddonList.ItemsSource = SelectedSet?.ArmorAddons
            .Select(k => FormKey.TryFactory(k) is { } fk && _addonsByKey.TryGetValue(fk, out var r) ? r.ToString() : k)
            .ToList();
    }

    private void OnRecordSearchChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var q = RecordSearch.Text.Trim();
        var kind = RecordKindBox.SelectedIndex; // 0 armor, 1 addon, 2 both
        var plugin = SelectedPlugin;
        if (_records.Count == 0 || (plugin == null && q.Length < 2))
        {
            RecordResults.ItemsSource = null;
            RecordCountText.Text = _records.Count == 0 ? "Load your game first." : "Pick a plugin, or type at least two letters.";
            return;
        }
        var hits = _records
            .Where(r => plugin == null || r.Key.ModKey == plugin)
            .Where(r => kind == 2 || (kind == 1) == r.IsAddon)
            .Where(r => q.Length == 0 || r.Matches(q))
            .OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RecordResults.ItemsSource = hits.Take(500).ToList();
        RecordCountText.Text = hits.Count > 500 ? $"{hits.Count} matches, first 500 shown" : $"{hits.Count} matches";
    }

    private const string AllPlugins = "(all plugins)";
    private List<string> _pluginNames = new();

    private ModKey? SelectedPlugin =>
        PluginBox.SelectedItem is string s && s != AllPlugins ? ModKey.FromFileName(s) : null;

    /// <summary>Plugins that define at least one armor or armor addon, for the plugin picker.</summary>
    private void RefreshPluginList()
    {
        _pluginNames = _records.Select(r => r.Key.ModKey.FileName.String).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        PluginBox.ItemsSource = new[] { AllPlugins }.Concat(_pluginNames).ToList();
        PluginBox.SelectedIndex = 0;
    }

    /// <summary>Typing in the picker narrows its list to matching plugin names.</summary>
    private void OnPluginBoxKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) return;
        var text = PluginBox.Text;
        if (PluginBox.SelectedItem is string chosen && chosen == text) return;
        var matches = _pluginNames.Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        _syncing = true;
        PluginBox.ItemsSource = new[] { AllPlugins }.Concat(matches).ToList();
        PluginBox.SelectedItem = null;
        PluginBox.Text = text;
        if (PluginBox.Template.FindName("PART_EditableTextBox", PluginBox) is TextBox box) box.CaretIndex = text.Length;
        PluginBox.IsDropDownOpen = true;
        _syncing = false;
    }

    private void OnPluginChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PluginBox.SelectedItem == null) return;
        OnRecordSearchChanged(sender, e);
    }

    /// <summary>Adds the picked record to the selected set; an armor brings all of its addons.</summary>
    private void OnAddRecord(object sender, RoutedEventArgs e)
    {
        if (RecordResults.SelectedItem is not RecordRow row) return;
        var set = SelectedSet ?? CreateSet(row.Name.Length > 0 ? row.Name : row.EditorId);
        var added = 0;
        foreach (var key in row.AddonKeys.Select(k => k.ToString()))
        {
            if (set.ArmorAddons.Contains(key)) continue;
            set.ArmorAddons.Add(key);
            added++;
        }
        Status($"Added {added} armor addon(s) from {row.EditorId} to \"{set.Name}\".");
        RefreshSetAddons();
        Recompute();
        RebuildPreview();
    }

    private void OnRemoveAddon(object sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { } set || SetAddonList.SelectedIndex < 0) return;
        set.ArmorAddons.RemoveAt(SetAddonList.SelectedIndex);
        RefreshSetAddons();
        Recompute();
        RebuildPreview();
    }

    private void OnImportPreset(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Piercing preset (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            foreach (var set in ToolSettings.LoadPreset(dlg.FileName))
            {
                set.Name = UniqueSetName(set.Name);
                _settings.Sets.Add(set);
            }
            RefreshSetList();
            Recompute();
        }
        catch (Exception ex)
        {
            Status("Could not read that preset: " + ex.Message);
        }
    }

    private void OnExportPreset(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Piercing preset (*.json)|*.json", FileName = "piercing-preset.json" };
        if (dlg.ShowDialog(this) == true) ToolSettings.SavePreset(dlg.FileName, _settings.Sets);
    }

    private string UniqueSetName(string wanted, PiercingSet? except = null)
    {
        var name = wanted;
        for (var i = 2; _settings.Sets.Any(s => s != except && s.Name == name); i++) name = $"{wanted} {i}";
        return name;
    }

    // ---------- output ----------

    private void OnOutputChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _settings.MergeIntoBody = MergeBox.IsChecked == true;
        _settings.StripReflections = StripReflectionsBox.IsChecked == true;
        RebuildPreview();
        var name = OutputNameBox.Text.Trim();
        if (name.Length == 0) name = "PiercingDistribution.esp";
        if (!name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) name += ".esp";
        _settings.OutputPluginName = name;
        OutputNameBox.Text = name;
        _settings.OutputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text) ? null : OutputFolderBox.Text.Trim();
    }

    private async void OnWritePatch(object sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var assignments = Distributor.Assign(_npcs, _settings);
        if (assignments.Count == 0)
        {
            Status("Nothing to write: no NPC has a set. Pick an NPC and choose a set under This NPC.");
            return;
        }
        var conflicts = _settings.MergeIntoBody ? new() : assignments
            .Select(a => (a.Npc, Warnings: SlotCheck.Conflicts(_session.LinkCache, a.Npc, a.Set)))
            .Where(x => x.Warnings.Count > 0)
            .ToList();
        if (conflicts.Count > 0)
        {
            var lines = conflicts.Take(12).Select(c => $"{c.Npc.Display}: {c.Warnings[0]}");
            var more = conflicts.Count > 12 ? $"\n…and {conflicts.Count - 12} more." : "";
            if (MessageBox.Show(this,
                    $"{conflicts.Count} NPC(s) have piercings that clash with a slot already in use:\n\n"
                    + string.Join("\n", lines) + more + "\n\nWrite the patch anyway?",
                    "Slot conflicts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        WriteButton.IsEnabled = false;
        Status($"Writing {assignments.Count} NPCs…");
        try
        {
            var session = _session;
            var result = await Task.Run(() => PatchWriter.Write(session, assignments, _settings));
            SaveSettings();
            Status($"Wrote {result.Path}: {result.NpcCount} NPCs, {result.SkinCount} skins"
                   + (result.MergedMeshes > 0 ? $", {result.MergedMeshes} merged body meshes" : "")
                   + (result.AddonOverrides > 0 ? $", {result.AddonOverrides} addon race fixes" : "")
                   + (result.Light ? ", flagged as a light plugin." : ".")
                   + (result.Problems.Count > 0 ? $" {result.Problems.Count} problem(s): " + string.Join(" ", result.Problems.Take(3)) : "")
                   + " Re-run after regenerating your NPC appearance patch"
                   + (result.MergedMeshes > 0 ? " or rebuilding bodies or piercings in BodySlide." : "."));
        }
        catch (Exception ex)
        {
            Status("Writing failed: " + ex.Message);
        }
        finally
        {
            WriteButton.IsEnabled = true;
        }
    }

    private void SaveSettings()
    {
        try { _settings.Save(); } catch { /* settings are a convenience */ }
    }

    private void Status(string text) => StatusText.Text = text;

    private sealed record NpcRow(NpcEntry Npc, string? SetName)
    {
        public string Display => Npc.Display;
        public string Detail => $"{Npc.RaceEditorId} · {(Npc.IsFemale ? "F" : "M")}{(Npc.IsUnique ? " · unique" : "")}"
                                + (SetName != null ? $" · {SetName}" : "")
                                + (Npc.SkipReason != null ? $" · skipped ({Npc.SkipReason})" : "");
    }

    /// <summary>An armor or armor addon from an enabled plugin, as the record search shows it.</summary>
    private sealed record RecordRow(FormKey Key, bool IsAddon, string EditorId, string Name, string Mesh, IReadOnlyList<FormKey> AddonKeys)
    {
        public static List<RecordRow> Collect(GameSession session)
        {
            var rows = new List<RecordRow>();
            foreach (var a in session.WinningArmors())
            {
                var addons = a.Armature.Select(x => x.FormKey).ToList();
                if (addons.Count == 0) continue;
                rows.Add(new RecordRow(a.FormKey, false, a.EditorID ?? "", a.Name?.String ?? "",
                    a.WorldModel?.Female?.Model?.File?.DataRelativePath.Path ?? a.WorldModel?.Male?.Model?.File?.DataRelativePath.Path ?? "",
                    addons));
            }
            foreach (var x in session.WinningArmorAddons())
                rows.Add(new RecordRow(x.FormKey, true, x.EditorID ?? "", "",
                    x.WorldModel?.Female?.File?.DataRelativePath.Path ?? x.WorldModel?.Male?.File?.DataRelativePath.Path ?? "",
                    new[] { x.FormKey }));
            return rows;
        }

        public bool Matches(string q) =>
            EditorId.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Mesh.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Key.ModKey.FileName.String.Contains(q, StringComparison.OrdinalIgnoreCase);

        public override string ToString() =>
            $"[{(IsAddon ? "ARMA" : "ARMO")}] {EditorId}{(Name.Length > 0 ? $" \"{Name}\"" : "")}  ·  {Key.ModKey.FileName}"
            + (IsAddon ? $"  ·  {Mesh}" : $"  ·  {AddonKeys.Count} addon(s)");
    }
}
