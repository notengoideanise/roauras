using RefugeCooldownOverlay.Core;

namespace RefugeCooldownOverlay.App;

/// <summary>
/// Configuration dialog. Edits overlay settings only; never sends input to PRM.
/// Shows every catalog skill with mapping status (searchable) and an explicit
/// ordered tile list with Up/Down reordering. Selection and order survive search
/// filtering. Created on demand by Program and disposed on close.
/// </summary>
public sealed class ConfigurationForm : Form
{
    private readonly OverlaySettings _settings;
    private readonly Func<IReadOnlyList<SkillDefinition>> _catalog;
    // persist=false applies live only (form stays open for further tweaking);
    // persist=true also writes settings.json before the caller closes the form.
    private readonly Action<OverlaySettings, IReadOnlyList<SkillDefinition>, bool> _apply;
    private readonly CheckedListBox _skills = new();
    private readonly ListBox _order = new();
    private readonly TextBox _search = new();
    private readonly NumericUpDown _columns = new();
    private readonly NumericUpDown _rows = new();
    private readonly NumericUpDown _iconSize = new();
    private readonly NumericUpDown _poll = new();
    private readonly TrackBar _opacity = new();
    private readonly ComboBox _backdrop = new();
    private readonly ComboBox _layout = new();
    private readonly CustomLayoutEditor _customEditor = new();
    private readonly CheckBox _clickThrough = new();
    private readonly Button _up = new();
    private readonly Button _down = new();

    // Selection state persists across search-filter repopulation: slugs stay
    // checked even while filtered out of the visible list. _itemSlugs parallels
    // the list-box items so duplicate display labels cannot cross-match.
    private readonly HashSet<string> _checkedSlugs;
    private readonly List<string> _itemSlugs = new();
    private bool _populating;

    public ConfigurationForm(
        OverlaySettings settings,
        Func<IReadOnlyList<SkillDefinition>> catalog,
        Action<OverlaySettings, IReadOnlyList<SkillDefinition>, bool> apply)
    {
        _settings = settings;
        _catalog = catalog;
        _apply = apply;
        _checkedSlugs = new HashSet<string>(settings.SelectedSkillSlugs);

        Text = "RoAuras — Configuration";
        Size = new Size(760, 940);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var searchLabel = new Label { Text = "Search:", Location = new Point(12, 15), AutoSize = true };
        _search.Location = new Point(70, 12);
        _search.Width = 300;
        _search.TextChanged += (_, _) => PopulateSkills();

        _skills.CheckOnClick = true;
        _skills.Location = new Point(12, 40);
        _skills.Size = new Size(470, 380);
        _skills.ItemCheck += (_, e) =>
        {
            if (_populating || e.Index < 0 || e.Index >= _itemSlugs.Count)
            {
                return;
            }

            var slug = _itemSlugs[e.Index];
            if (e.NewValue == CheckState.Checked)
            {
                if (_checkedSlugs.Add(slug))
                {
                    _order.Items.Add(DisplayName(slug));
                    SyncCustomEditor();
                }
            }
            else if (_checkedSlugs.Remove(slug))
            {
                _order.Items.Remove(DisplayName(slug));
                SyncCustomEditor();
            }
        };

        var orderLabel = new Label { Text = "Tile order:", Location = new Point(496, 15), AutoSize = true };
        _order.Location = new Point(496, 40);
        _order.Size = new Size(240, 300);

        _up.Text = "↑ Up";
        _up.Location = new Point(496, 348);
        _up.Size = new Size(112, 28);
        _up.Click += (_, _) => MoveOrder(-1);

        _down.Text = "↓ Down";
        _down.Location = new Point(624, 348);
        _down.Size = new Size(112, 28);
        _down.Click += (_, _) => MoveOrder(1);

        var columnsLabel = new Label { Text = "Columns across:", Location = new Point(12, 432), AutoSize = true };
        _columns.Minimum = 1;
        _columns.Maximum = 12;
        _columns.Value = _settings.Columns;
        _columns.Location = new Point(120, 428);

        var rowsLabel = new Label { Text = "Rows down (0 = auto):", Location = new Point(250, 432), AutoSize = true };
        _rows.Minimum = 0;
        _rows.Maximum = 12;
        _rows.Value = _settings.Rows;
        _rows.Location = new Point(400, 428);

        var iconLabel = new Label { Text = "Icon size (px):", Location = new Point(12, 466), AutoSize = true };
        _iconSize.Minimum = 16;
        _iconSize.Maximum = 128;
        _iconSize.Value = _settings.IconSize;
        _iconSize.Location = new Point(110, 462);

        var pollLabel = new Label { Text = "Poll interval (ms):", Location = new Point(250, 466), AutoSize = true };
        _poll.Minimum = 25;
        _poll.Maximum = 1000;
        _poll.Value = _settings.PollMilliseconds;
        _poll.Location = new Point(390, 462);

        var opacityLabel = new Label { Text = "Opacity:", Location = new Point(12, 500), AutoSize = true };
        _opacity.Minimum = 20;
        _opacity.Maximum = 100;
        _opacity.Value = (int)(_settings.Opacity * 100);
        _opacity.Width = 300;
        _opacity.Location = new Point(90, 496);

        var backdropLabel = new Label { Text = "Backdrop:", Location = new Point(410, 500), AutoSize = true };
        _backdrop.DropDownStyle = ComboBoxStyle.DropDownList;
        _backdrop.Items.AddRange(Enum.GetNames<OverlayBackdropStyle>());
        _backdrop.SelectedItem = _settings.Backdrop.ToString();
        _backdrop.Location = new Point(470, 496);
        _backdrop.Width = 120;

        var layoutLabel = new Label { Text = "Layout:", Location = new Point(604, 500), AutoSize = true };
        _layout.DropDownStyle = ComboBoxStyle.DropDownList;
        _layout.Items.AddRange(Enum.GetNames<OverlayLayoutPreset>());
        _layout.SelectedItem = _settings.LayoutPreset.ToString();
        _layout.Location = new Point(652, 496);
        _layout.Width = 84;

        var customLabel = new Label
        {
            Text = "Custom layout: drag individual icons; empty cells are allowed.",
            Location = new Point(12, 560),
            AutoSize = true,
        };
        _customEditor.Location = new Point(12, 584);
        _customEditor.Size = new Size(470, 300);
        _customEditor.SetSelection(
            SkillCatalog.SelectInOrder(_catalog(), _settings.SelectedSkillSlugs),
            _settings.CustomCells);

        _clickThrough.Text = "Click-through (display mode)";
        _clickThrough.Checked = _settings.ClickThrough;
        _clickThrough.Location = new Point(12, 530);
        _clickThrough.AutoSize = true;

        // Apply commits settings live and keeps this dialog open for tweaking;
        // Save persists and closes. Configuration mode remains active after both.
        var applyButton = new Button { Text = "Apply", Location = new Point(496, 610), Size = new Size(116, 30) };
        applyButton.Click += (_, _) => ApplyAndClose(persist: false, close: false);

        var saveButton = new Button { Text = "Save", Location = new Point(620, 610), Size = new Size(116, 30) };
        saveButton.Click += (_, _) => ApplyAndClose(persist: true, close: true);
        _layout.SelectedIndexChanged += (_, _) => ShowCustomEditor();

        Controls.AddRange(new Control[]
        {
            searchLabel, _search, _skills,
            orderLabel, _order, _up, _down,
            columnsLabel, _columns, rowsLabel, _rows,
            iconLabel, _iconSize, pollLabel, _poll,
            opacityLabel, _opacity, backdropLabel, _backdrop,
            layoutLabel, _layout,
            _clickThrough, customLabel, _customEditor, applyButton, saveButton,
        });

        PopulateSkills();
        PopulateOrder();
        ShowCustomEditor();
        DarkTheme.Apply(this);
    }

    private void SyncCustomEditor()
    {
        var selected = SkillCatalog.SelectInOrder(_catalog(), OrderedSlugs());
        _customEditor.UpdateSelection(selected);
    }

    private void ShowCustomEditor()
    {
        var enabled = _layout.SelectedItem?.ToString() == nameof(OverlayLayoutPreset.Custom);
        _customEditor.Enabled = enabled;
        _customEditor.BackColor = enabled
            ? DarkTheme.ToColor(OverlayTheme.SurfaceControl)
            : DarkTheme.ToColor(OverlayTheme.Surface);
        _customEditor.Invalidate();
    }

    private string DisplayName(string slug) =>
        _catalog().FirstOrDefault(s => s.Slug == slug) is { } skill
            ? $"{skill.Name} — {skill.ClassName} [{skill.Slug}]"
            : slug;

    private void PopulateSkills()
    {
        _populating = true;
        _skills.BeginUpdate();
        _skills.Items.Clear();
        _itemSlugs.Clear();
        var filter = _search.Text.Trim();
        foreach (var skill in _catalog())
        {
            if (filter.Length > 0 &&
                !skill.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !skill.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = $"[{skill.MappingStatus}] {skill.Name} — {skill.ClassName}"
                + (skill.RuntimeKey is int key ? $" (key {key})" : string.Empty);
            var index = _skills.Items.Add(label);
            _itemSlugs.Add(skill.Slug);
            _skills.SetItemChecked(index, _checkedSlugs.Contains(skill.Slug));
        }

        _skills.EndUpdate();
        _populating = false;
    }

    private void PopulateOrder()
    {
        _order.BeginUpdate();
        _order.Items.Clear();
        // Persisted order first, then any checked slugs missing from it (added
        // while this dialog was open), so nothing silently disappears.
        foreach (var slug in _settings.SelectedSkillSlugs.Where(_checkedSlugs.Contains))
        {
            _order.Items.Add(DisplayName(slug));
        }

        foreach (var slug in _checkedSlugs.Where(s => !_settings.SelectedSkillSlugs.Contains(s)))
        {
            _order.Items.Add(DisplayName(slug));
        }

        _order.EndUpdate();
    }

    private List<string> OrderedSlugs()
    {
        var catalogByDisplay = new Dictionary<string, string>();
        foreach (var skill in _catalog())
        {
            catalogByDisplay[$"{skill.Name} — {skill.ClassName} [{skill.Slug}]"] = skill.Slug;
            catalogByDisplay[skill.Slug] = skill.Slug;
        }

        return _order.Items.Cast<string>()
            .Select(name => catalogByDisplay.TryGetValue(name, out var slug) ? slug : null)
            .OfType<string>()
            .Distinct()
            .ToList();
    }

    private void MoveOrder(int direction)
    {
        var index = _order.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var target = index + direction;
        if (target < 0 || target >= _order.Items.Count)
        {
            return;
        }

        var item = _order.Items[index]!;
        _order.Items[index] = _order.Items[target]!;
        _order.Items[target] = item;
        _order.SelectedIndex = target;
        SyncCustomEditor();
    }

    private void ApplyAndClose(bool persist, bool close)
    {
        _settings.Columns = (int)_columns.Value;
        _settings.Rows = (int)_rows.Value;
        _settings.IconSize = (int)_iconSize.Value;
        _settings.PollMilliseconds = (int)_poll.Value;
        _settings.Opacity = _opacity.Value / 100.0;
        _settings.Backdrop = Enum.TryParse<OverlayBackdropStyle>(_backdrop.SelectedItem?.ToString(), out var backdrop)
            ? backdrop
            : OverlayBackdropStyle.Dark;
        _settings.ClickThrough = _clickThrough.Checked;
        _settings.LayoutPreset = Enum.TryParse<OverlayLayoutPreset>(_layout.SelectedItem?.ToString(), out var preset)
            ? preset
            : OverlayLayoutPreset.Grid;
        _settings.CustomCells = _customEditor.Cells.ToList();

        // Tile order is the persisted slug order; any checked skill missing from
        // the order list (added via search) is appended in catalog order.
        var ordered = OrderedSlugs();
        foreach (var slug in _checkedSlugs.Where(s => !ordered.Contains(s)))
        {
            ordered.Add(slug);
        }

        _settings.SelectedSkillSlugs = ordered;
        _apply(_settings, SkillCatalog.SelectInOrder(_catalog(), ordered), persist);
        if (close)
        {
            Close();
        }
    }
}
