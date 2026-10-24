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
    private List<AddonRow> _allAddons = new();
    private bool _syncing;

    private const string AssignedByRules = "(as the rules decide)";
    private const string ForcedNone = "(none)";

    public MainWindow()
    {
        InitializeComponent();
        _syncing = true;
        DataFolderBox.Text = _settings.DataFolder ?? "";
        OutputNameBox.Text = _settings.OutputPluginName;
        OutputFolderBox.Text = _settings.OutputFolder ?? "";
        ChanceSlider.Value = _settings.Rules.ChancePercent;
        UniqueBox.IsChecked = _settings.Rules.IncludeUnique;
        GenericBox.IsChecked = _settings.Rules.IncludeGeneric;
        _syncing = false;
        RefreshRulesLabels();
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
            var (session, npcs, addons) = await Task.Run(() =>
            {
                var s = GameSession.Open(folder, ownPatch);
                var n = NpcEntry.Collect(s);
                var a = s.WinningArmorAddons()
                    .Select(x => new AddonRow(x.FormKey, x.EditorID ?? "",
                        x.WorldModel?.Female?.File?.DataRelativePath.Path ?? x.WorldModel?.Male?.File?.DataRelativePath.Path ?? ""))
                    .ToList();
                return (s, n, a);
            });
            _session?.Dispose();
            _session = session;
            _npcs = npcs;
            _allAddons = addons;
            var assets = new AssetResolver(session.DataFolder);
            _builder = new NpcModelBuilder(session, assets);
            _textures = new TextureLoader(assets);
            _settings.DataFolder = folder;
            WriteButton.IsEnabled = true;
            Recompute();
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
        NpcList.ItemsSource = rows;
        if (selected != null) NpcList.SelectedItem = rows.FirstOrDefault(r => r.Npc.FormKey == selected);
        NpcCountText.Text = $"{rows.Count} shown · {_assigned.Count} will get piercings";
    }

    private void OnNpcFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshNpcList();
    }

    private void OnNpcSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = NpcList.SelectedItem as NpcRow;
        _syncing = true;
        var overrideItems = new List<string> { AssignedByRules, ForcedNone };
        overrideItems.AddRange(_settings.Sets.Select(s => s.Name));
        NpcOverrideBox.ItemsSource = overrideItems;
        if (row != null && _settings.Rules.Overrides.TryGetValue(row.Npc.FormKey.ToString(), out var forced))
            NpcOverrideBox.SelectedItem = forced == "" ? ForcedNone : forced;
        else NpcOverrideBox.SelectedItem = AssignedByRules;

        var previewItems = new List<string> { ForcedNone };
        previewItems.AddRange(_settings.Sets.Select(s => s.Name));
        PreviewSetBox.ItemsSource = previewItems;
        PreviewSetBox.SelectedItem = row?.SetName ?? ForcedNone;
        _syncing = false;
        RebuildPreview();
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
    }

    private PiercingSet? SelectedSet => SetList.SelectedItem as PiercingSet;

    private void OnAddSet(object sender, RoutedEventArgs e)
    {
        var set = new PiercingSet { Name = UniqueSetName("New set") };
        _settings.Sets.Add(set);
        RefreshSetList(set);
        Recompute();
    }

    private void OnRemoveSet(object sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { } set) return;
        _settings.Sets.Remove(set);
        RefreshSetList();
        Recompute();
    }

    private void OnSetSelected(object sender, SelectionChangedEventArgs e)
    {
        var set = SelectedSet;
        SetEditor.IsEnabled = set != null;
        _syncing = true;
        SetNameBox.Text = set?.Name ?? "";
        SetGenderBox.SelectedIndex = set == null ? 0 : (int)set.Gender;
        SetRacesBox.Text = set == null ? "" : string.Join(", ", set.Races);
        SetWeightBox.Text = set?.Weight.ToString() ?? "";
        _syncing = false;
        RefreshSetAddons();
    }

    private void OnSetFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || SelectedSet is not { } set) return;
        var newName = SetNameBox.Text.Trim();
        if (newName.Length > 0 && newName != set.Name)
        {
            newName = UniqueSetName(newName, set);
            foreach (var key in _settings.Rules.Overrides.Where(kv => kv.Value == set.Name).Select(kv => kv.Key).ToList())
                _settings.Rules.Overrides[key] = newName;
            set.Name = newName;
        }
        set.Gender = (GenderFilter)Math.Max(0, SetGenderBox.SelectedIndex);
        set.Races = SetRacesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (int.TryParse(SetWeightBox.Text, out var w)) set.Weight = Math.Max(0, w);
        RefreshSetList(set);
        Recompute();
    }

    private void RefreshSetAddons()
    {
        SetAddonList.ItemsSource = SelectedSet?.ArmorAddons.Select(DescribeAddon).ToList();
    }

    private string DescribeAddon(string key)
    {
        var fk = FormKey.TryFactory(key);
        var row = fk == null ? null : _allAddons.FirstOrDefault(a => a.Key == fk);
        return row?.ToString() ?? key;
    }

    private void OnAddonSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = AddonSearch.Text.Trim();
        AddonResults.ItemsSource = q.Length < 2 ? null : _allAddons
            .Where(a => a.EditorId.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || a.Mesh.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || a.Key.ModKey.FileName.String.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(300).ToList();
    }

    private void OnAddAddon(object sender, RoutedEventArgs e)
    {
        if (SelectedSet is not { } set || AddonResults.SelectedItem is not AddonRow row) return;
        var key = row.Key.ToString();
        if (!set.ArmorAddons.Contains(key)) set.ArmorAddons.Add(key);
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

    // ---------- rules ----------

    private void OnRulesChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        _settings.Rules.ChancePercent = ChanceSlider.Value;
        _settings.Rules.IncludeUnique = UniqueBox.IsChecked == true;
        _settings.Rules.IncludeGeneric = GenericBox.IsChecked == true;
        RefreshRulesLabels();
        Recompute();
    }

    private void OnReshuffle(object sender, RoutedEventArgs e)
    {
        _settings.Rules.Seed++;
        RefreshRulesLabels();
        Recompute();
    }

    private void RefreshRulesLabels()
    {
        ChanceLabel.Text = $"Chance: {_settings.Rules.ChancePercent:0}%";
        SeedLabel.Text = $"Seed: {_settings.Rules.Seed}";
    }

    private void OnNpcOverrideChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || NpcList.SelectedItem is not NpcRow row || NpcOverrideBox.SelectedItem is not string choice) return;
        var key = row.Npc.FormKey.ToString();
        if (choice == AssignedByRules) _settings.Rules.Overrides.Remove(key);
        else _settings.Rules.Overrides[key] = choice == ForcedNone ? "" : choice;
        Recompute();
    }

    // ---------- output ----------

    private void OnOutputChanged(object sender, RoutedEventArgs e)
    {
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
            Status("Nothing to write: no NPC is assigned a set. Add a set with armor addons, or raise the chance.");
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
                   + (result.AddonOverrides > 0 ? $", {result.AddonOverrides} addon race fixes" : "")
                   + (result.Light ? ", flagged as a light plugin." : ".")
                   + " Re-run after regenerating your NPC appearance patch.");
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

    private sealed record AddonRow(FormKey Key, string EditorId, string Mesh)
    {
        public override string ToString() => $"{EditorId}  [{Key}]  {Mesh}";
    }
}
