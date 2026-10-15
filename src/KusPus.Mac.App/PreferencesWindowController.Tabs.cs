#pragma warning disable CA1848
#pragma warning disable CA1873

using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.Logging;

namespace KusPus.Mac.App;

/// <summary>Models, History, Privacy and About tabs.</summary>
internal sealed partial class PreferencesWindowController
{
    // ── Models ──────────────────────────────────────────────────────────────
    //
    // Cards grouped by publisher (NVIDIA / OpenAI), recommended group and model first.
    // Collapsed: name + chips, release + parameters, a plain-English description, Speed and
    // Accuracy dot scales, languages, size, and the one action. "Details" expands the
    // power-user facts (parameters, weights, files, source, licence, leaderboard WER,
    // location) plus Show in Finder / Remove. All facts come from ModelInfo in models.json.

    private const string LeaderboardUrl = "https://huggingface.co/spaces/hf-audio/open_asr_leaderboard";

    private static string EngineLabel(ModelDescriptor m) => m.Engine == ModelEngine.Parakeet ? "Parakeet" : "Whisper";

    private sealed class ModelDownloadState
    {
        public CancellationTokenSource? Cts;
        public double Percent;
        public long BytesTotal;
        public string? Error;
    }

    private readonly Dictionary<string, ModelDownloadState> _modelDownloads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedModels = new(StringComparer.Ordinal);
    private NSTextField? _modelsDiskText;
    private NSStackView? _modelsList;
    private bool _modelsRendered;

    private NSStackView BuildModelsTab()
    {
        _modelsDiskText = UI.Label("—", Fonts.SupportingNumeric, "MutedText");
        var detail = UI.HStack(8, _modelsDiskText, Settings.Link("Show in Finder", OpenModelsFolder), UI.Spacer());
        var intro = Settings.Intro("Choose the model that turns your voice into text. Every model runs on your Mac.", detail);
        _modelsList = UI.VStack(0);
        _modelsRendered = false;
        return Tab(intro, _modelsList);
    }

    private void RenderModelsList()
    {
        if (_modelsList is null || _modelsDiskText is null)
        {
            return;
        }
        _modelsRendered = true;
        var activeId = _prefs.Current.Models.ActiveModelId;
        var manifest = _models.Manifest;

        long downloaded = manifest.Models.Sum(_models.DownloadedSizeBytes);
        _modelsDiskText.StringValue = downloaded > 0
            ? $"{UI.FormatSize(downloaded)} of models downloaded."
            : "Only the built-in model is installed.";

        foreach (var v in _modelsList.ArrangedSubviews.ToArray())
        {
            _modelsList.RemoveArrangedSubview(v);
            v.RemoveFromSuperview();
        }
        // One section per publisher; the publisher of the recommended model first, and
        // inside a section the recommended model first, then smallest to largest.
        var groups = manifest.Models
            .GroupBy(m => m.Info?.Maker ?? EngineLabel(m), StringComparer.Ordinal)
            .OrderByDescending(g => g.Any(m => m.Info?.Recommended == true))
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var list = group.OrderByDescending(m => m.Info?.Recommended == true).ThenBy(m => m.SizeBytes).ToList();
            var sample = list[0];
            var licences = list.Select(m => m.Info?.License).OfType<string>().Distinct(StringComparer.Ordinal);
            string family = sample.Engine == ModelEngine.Parakeet ? "Parakeet" : "Whisper";
            string runtime = sample.Engine == ModelEngine.Parakeet ? "runs in-process" : "runs with whisper.cpp on Metal";
            var rows = list.Select(m => BuildModelRow(m, _models.IsInstalled(m), m.Id == activeId)).ToArray();
            var section = Settings.Section($"{group.Key} {family}", $"{string.Join(" / ", licences)} licence · {runtime}", Settings.Group(rows));
            _modelsList.AddArrangedSubview(section);
            section.WidthAnchor.ConstraintEqualTo(_modelsList.WidthAnchor).Active = true;
        }
    }

    // States per row: In use / Installed / Not installed / Downloading / Failed. No accent
    // bars or tinted cards: the state is the trailing control (status text or one button).
    private SettingsRow BuildModelRow(ModelDescriptor m, bool installed, bool isActive)
    {
        _modelDownloads.TryGetValue(m.Id, out var ds);
        bool isDownloading = ds?.Cts is not null;
        bool isFailed = ds?.Error is not null && ds.Cts is null;
        var info = m.Info;

        var badges = UI.HStack(4);
        if (info?.Recommended == true)
        {
            badges.AddArrangedSubview(Settings.Badge("Recommended", accent: true));
        }
        if (m.Bundled)
        {
            badges.AddArrangedSubview(Settings.Badge("Built in", accent: false));
        }
        var action = BuildModelActionRegion(m, installed, isActive, isDownloading, isFailed, ds);
        var row = new SettingsRow(m.DisplayName, info?.Description ?? $"{EngineLabel(m)} · {UI.FormatSize(m.SizeBytes)}", action,
            badges.ArrangedSubviews.Length > 0 ? badges : null);

        if (info is not null)
        {
            var facts = UI.HStack(16,
                Rating("Speed", info.SpeedTier),
                Rating("Accuracy", info.AccuracyTier),
                Fact(info.Languages),
                Fact(UI.FormatSize(m.SizeBytes)),
                UI.Spacer());
            row.AddBelow(facts);

            bool expanded = _expandedModels.Contains(m.Id);
            var toggle = Settings.Disclosure(expanded, () =>
            {
                if (!_expandedModels.Remove(m.Id))
                {
                    _expandedModels.Add(m.Id);
                }
                RenderModelsList();
            });
            row.AddBelow(UI.HStack(0, toggle, UI.Spacer()));
            if (expanded)
            {
                row.AddBelow(BuildModelDetails(m, info, installed, isActive));
            }
        }
        return row;
    }

    /// <summary>Facts for people who want them: a key/value list plus Show in Finder / Remove.</summary>
    private NSStackView BuildModelDetails(ModelDescriptor m, ModelInfo info, bool installed, bool isActive)
    {
        var rows = UI.VStack(4);
        rows.AddArrangedSubview(Spec("Released", info.Released));
        rows.AddArrangedSubview(Spec("Parameters", info.Parameters));
        rows.AddArrangedSubview(Spec("Weights", info.Quantization));
        rows.AddArrangedSubview(Spec("Files", m.Files.Count == 1
            ? m.Files[0].FileName
            : $"{m.Files.Count} files · {string.Join(", ", m.Files.Select(f => f.FileName))}"));
        var sourceHost = Uri.TryCreate(m.PrimaryFile.Url, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath.Split("/resolve/")[0] : m.PrimaryFile.Url;
        rows.AddArrangedSubview(Spec("Source", sourceHost, m.PrimaryFile.Url));
        rows.AddArrangedSubview(Spec("Model card", info.ModelCardUrl.Replace("https://", string.Empty, StringComparison.Ordinal), info.ModelCardUrl));
        rows.AddArrangedSubview(Spec("Licence", info.License, info.LicenseUrl));
        rows.AddArrangedSubview(info.LeaderboardWer is { } wer
            ? Spec("Error rate", $"{wer:F2}% average WER, Open ASR leaderboard", LeaderboardUrl)
            : Spec("Error rate", "Compare on the Open ASR leaderboard", LeaderboardUrl));
        long downloaded = _models.DownloadedSizeBytes(m);
        if (downloaded > 0)
        {
            rows.AddArrangedSubview(Spec("Location", Path.Combine(_models.ModelsDirectory, m.Subdirectory), truncateMiddle: true));
        }
        else if (installed && m.Bundled)
        {
            rows.AddArrangedSubview(Spec("Location", "Inside the KusPus app"));
        }

        var block = UI.VStack(12, rows);
        if (downloaded > 0)
        {
            var actions = UI.HStack(8, UI.Button("Show in Finder", UI.ButtonKind.Secondary, () => RevealModel(m)));
            actions.AddArrangedSubview(isActive
                ? UI.Label("Switch to another model to remove this one.", Fonts.Supporting, "MutedText")
                : Settings.Destructive($"Remove {UI.FormatSize(downloaded)}", () => ConfirmRemoveModel(m, downloaded)));
            block.AddArrangedSubview(actions);
        }
        rows.WidthAnchor.ConstraintEqualTo(block.WidthAnchor).Active = true;
        block.EdgeInsets = new NSEdgeInsets(4, 0, 0, 0);
        return block;
    }

    private static NSStackView Spec(string key, string value, string? url = null, bool truncateMiddle = false)
    {
        var k = UI.Label(key, Fonts.Supporting, "MutedText");
        k.WidthAnchor.ConstraintEqualTo(88).Active = true;
        NSView v;
        if (truncateMiddle)
        {
            var path = UI.Label(value, Fonts.Supporting, "SecondaryText");
            path.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
            path.ToolTip = value;
            v = path;
        }
        else if (url is null)
        {
            v = UI.Label(value, Fonts.Supporting, "SecondaryText", wraps: true);
        }
        else
        {
            var link = Settings.Link(value, () => UI.OpenUrl(url), external: true);
            link.ToolTip = url;
            v = link;
        }
        var row = UI.HStack(8, k, v);
        row.Alignment = NSLayoutAttribute.FirstBaseline;
        return row;
    }

    /// <summary>"Speed ●●●●○": five dots on the same scale for every model; VoiceOver reads "Speed, 4 of 5".</summary>
    private static NSStackView Rating(string label, int tier)
    {
        var stack = UI.HStack(4, UI.Label(label, Fonts.Supporting, "MutedText"));
        stack.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        for (int i = 1; i <= 5; i++)
        {
            var dot = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            dot.WidthAnchor.ConstraintEqualTo(8).Active = true;
            dot.HeightAnchor.ConstraintEqualTo(8).Active = true;
            dot.Layer!.CornerRadius = 4;
            dot.Layer.BackgroundColor = i <= tier ? ThemeColors.Cg("MintText") : ThemeColors.Cg("RatingEmpty");
            stack.AddArrangedSubview(dot);
        }
        stack.AccessibilityElement = true;
        stack.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        stack.AccessibilityLabel = $"{label}, {tier} of 5";
        stack.ToolTip = $"{label}: {tier} of 5, compared with the other models here";
        return stack;
    }

    private static NSTextField Fact(string text) => UI.Label(text, Fonts.SupportingNumeric, "SecondaryText");

    private NSView BuildModelActionRegion(ModelDescriptor m, bool installed, bool isActive, bool isDownloading, bool isFailed, ModelDownloadState? ds)
    {
        if (isActive)
        {
            return Settings.Status(StatusKind.Ok, "In use");
        }
        if (isFailed && ds is not null)
        {
            var err = UI.Label(ds.Error!, Fonts.Supporting, "ErrorText", wraps: true);
            err.WidthAnchor.ConstraintLessThanOrEqualTo(224).Active = true;
            err.Alignment = NSTextAlignment.Right;
            return UI.HStack(8, err, UI.Button("Try again", UI.ButtonKind.Secondary, () => StartModelDownload(m)));
        }
        if (isDownloading && ds is not null)
        {
            var bar = new NSProgressIndicator
            {
                Style = NSProgressIndicatorStyle.Bar,
                Indeterminate = false,
                MinValue = 0,
                MaxValue = 100,
                DoubleValue = ds.Percent,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            bar.WidthAnchor.ConstraintEqualTo(160).Active = true;
            Settings.AxLabel(bar, $"Downloading {m.DisplayName}");
            var pct = UI.Label($"{ds.Percent:F0}%", Fonts.SupportingNumeric, "SecondaryText");
            pct.WidthAnchor.ConstraintEqualTo(40).Active = true;
            pct.Alignment = NSTextAlignment.Right;
            return UI.HStack(8, bar, pct, UI.Button("Cancel", UI.ButtonKind.Secondary, () => _modelDownloads.GetValueOrDefault(m.Id)?.Cts?.Cancel()));
        }
        // The recommended model is the page's one primary action until it's in use.
        bool primary = m.Info?.Recommended == true;
        if (installed)
        {
            var use = UI.Button("Use", primary ? UI.ButtonKind.Primary : UI.ButtonKind.Secondary, () => _ = OnUseModelAsync(m.Id));
            Settings.AxLabel(use, $"Use {m.DisplayName}");
            return use;
        }
        var download = UI.Button($"Download {UI.FormatSize(m.SizeBytes)}", primary ? UI.ButtonKind.Primary : UI.ButtonKind.Secondary, () => StartModelDownload(m));
        Settings.AxLabel(download, $"Download {m.DisplayName}, {UI.FormatSize(m.SizeBytes)}");
        return download;
    }

    private void OpenModelsFolder()
    {
        Directory.CreateDirectory(_models.ModelsDirectory);
        NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.FromFilename(_models.ModelsDirectory));
    }

    private void RevealModel(ModelDescriptor m)
    {
        var dir = Path.Combine(_models.ModelsDirectory, m.Subdirectory);
        var primary = Path.Combine(dir, m.PrimaryFile.FileName);
        if (File.Exists(primary))
        {
            NSWorkspace.SharedWorkspace.SelectFile(primary, dir);
        }
        else
        {
            NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.FromFilename(dir));
        }
    }

    private void ConfirmRemoveModel(ModelDescriptor m, long bytes)
    {
        var alert = new NSAlert
        {
            MessageText = $"Remove {m.DisplayName}?",
            InformativeText = $"This frees {UI.FormatSize(bytes)}. You can download it again any time.",
            AlertStyle = NSAlertStyle.Warning,
        };
        alert.AddButton("Remove").HasDestructiveAction = true;
        alert.AddButton("Cancel");
        alert.BeginSheet(_window, response =>
        {
            if ((long)response != 1000) // NSAlertFirstButtonReturn
            {
                return;
            }
            var result = _models.Remove(m);
            if (!result.Success)
            {
                _modelDownloads[m.Id] = new ModelDownloadState { Error = result.Error ?? "Couldn't remove the model." };
            }
            RenderModelsList();
        });
    }

    private async Task OnUseModelAsync(string id)
    {
        var current = _prefs.Current;
        if (current.Models.ActiveModelId == id)
        {
            return;
        }
        _logger.LogInformation("Active model changed → {Id}.", id);
        try
        {
            await _prefs.UpdateAsync(s => s with { Models = s.Models with { ActiveModelId = id } }).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for ActiveModelId.");
            return;
        }
        RenderModelsList();
    }

    private void StartModelDownload(ModelDescriptor descriptor)
    {
        if (_prefs.Current.Privacy.OfflineMode)
        {
            _modelDownloads[descriptor.Id] = new ModelDownloadState { Error = "Disabled while Offline Mode is on." };
            RenderModelsList();
            return;
        }
        var cts = new CancellationTokenSource();
        var state = new ModelDownloadState { Cts = cts, BytesTotal = descriptor.SizeBytes };
        _modelDownloads[descriptor.Id] = state;
        RenderModelsList();

        // IProgress<T> captures the main-thread SynchronizationContext; throttle to ~0.5 % steps.
        var progress = new Progress<DownloadProgress>(p =>
        {
            var total = p.TotalBytes > 0 ? p.TotalBytes : descriptor.SizeBytes;
            double pct = total > 0 ? (double)p.BytesDownloaded / total * 100.0 : 0;
            if (Math.Abs(pct - state.Percent) < 0.5)
            {
                return;
            }
            state.Percent = pct;
            RenderModelsList();
        });

        _ = Task.Run(async () =>
        {
            var result = await _models.DownloadAsync(descriptor, progress, cts.Token).ConfigureAwait(false);
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (result.Success)
                {
                    _modelDownloads.Remove(descriptor.Id);
                    _logger.LogInformation("Model {Id} downloaded.", descriptor.Id);
                }
                else if (cts.IsCancellationRequested)
                {
                    _modelDownloads.Remove(descriptor.Id);
                }
                else
                {
                    state.Cts = null;
                    state.Error = ShortenDownloadError(result.Error ?? "Download failed.");
                }
                cts.Dispose();
                RenderModelsList();
            });
        });
    }

    private static string ShortenDownloadError(string raw)
    {
        const string prefix = "HTTP error downloading ";
        if (raw.StartsWith(prefix, StringComparison.Ordinal))
        {
            int colon = raw.IndexOf(':', prefix.Length);
            if (colon > 0 && colon + 2 < raw.Length)
            {
                return raw[(colon + 2)..];
            }
        }
        return raw.Length > 80 ? raw[..77] + "…" : raw;
    }

    // ── History ─────────────────────────────────────────────────────────────

    private NSSearchField? _historySearch;
    private NSStackView? _historyList;
    private NSTextField? _historyStats;
    private NSButton? _purgeButton;
    private NSTimer? _historyDebounce;
    private string _historyQuery = string.Empty;
    private bool _historyRendered;
    // At most one row shows hover; a lost MouseExited can't leave others stuck.
    private HistoryRowView? _hoveredRow;

    // Status · Time · App · Transcript (flex) · Model · Length — all on the 4 pt grid.
    private static readonly double[] HistoryColumns = [16, 72, 112, 0, 88, 48];

    private NSStackView BuildHistoryTab()
    {
        _historySearch = new NSSearchField { PlaceholderString = "Search transcripts", TranslatesAutoresizingMaskIntoConstraints = false };
        _historySearch.Font = Fonts.Body;
        _historySearch.Changed += (_, _) => OnHistorySearchChanged();
        // List toolbar: search (flex) · count · Delete all… — the bulk action sits with the
        // list it acts on instead of 50 rows down; destructive tint + confirm sheet.
        _historyStats = UI.Label("—", Fonts.SupportingNumeric, "MutedText");
        _historyStats.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _purgeButton = Settings.Destructive("Delete all…", OnPurgeAllClick);
        _historySearch.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var toolbar = UI.HStack(12, _historySearch, _historyStats, _purgeButton);
        toolbar.SetCustomSpacing(16, _historyStats);
        var searchBar = Padded(toolbar, Settings.RowPadX, Settings.RowPadY);

        var header = HistoryGrid(
            new NSView(),
            ColumnLabel("Time"), ColumnLabel("App"), ColumnLabel("Transcript"), ColumnLabel("Model"), RightAligned(ColumnLabel("Length")));
        var headerBox = Padded(header, Settings.RowPadX, 8);

        _historyList = UI.VStack(0);

        var column = UI.VStack(0, searchBar, Hairline(), headerBox, Hairline(), _historyList);
        foreach (var v in column.ArrangedSubviews)
        {
            v.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        var card = new CardView(column, "Surface", "BorderSubtle", radius: Settings.CardRadius, padX: 0, padY: 0);
        card.Layer!.MasksToBounds = true;
        _historyRendered = false;
        return Tab(Settings.Section("History", card));
    }

    private static NSTextField ColumnLabel(string text) => UI.Label(text, Fonts.Caption, "MutedText");

    private static NSView Hairline()
    {
        var v = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        v.Layer!.BackgroundColor = ThemeColors.Cg("Separator");
        v.HeightAnchor.ConstraintEqualTo(1).Active = true;
        return v;
    }

    private static FlippedView Padded(NSView content, double padX, double padY)
    {
        var box = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        box.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.LeadingAnchor.ConstraintEqualTo(box.LeadingAnchor, (nfloat)padX),
            content.TrailingAnchor.ConstraintEqualTo(box.TrailingAnchor, (nfloat)(-padX)),
            content.TopAnchor.ConstraintEqualTo(box.TopAnchor, (nfloat)padY),
            content.BottomAnchor.ConstraintEqualTo(box.BottomAnchor, (nfloat)(-padY)),
        ]);
        return box;
    }

    private static NSTextField RightAligned(NSTextField tf)
    {
        tf.Alignment = NSTextAlignment.Right;
        return tf;
    }

    /// <summary>Six-column row (16 / 72 / 112 / * / 88 / 48) with 12 pt gutters.</summary>
    private static NSStackView HistoryGrid(params NSView[] cells)
    {
        var row = UI.HStack(12, cells);
        for (int i = 0; i < cells.Length && i < HistoryColumns.Length; i++)
        {
            cells[i].TranslatesAutoresizingMaskIntoConstraints = false;
            if (HistoryColumns[i] > 0)
            {
                cells[i].WidthAnchor.ConstraintEqualTo((nfloat)HistoryColumns[i]).Active = true;
                cells[i].SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
            }
            else
            {
                cells[i].SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
                cells[i].SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
            }
        }
        return row;
    }

    private async Task ReloadHistoryAsync()
    {
        if (_historyList is null || _historyStats is null || _purgeButton is null)
        {
            return;
        }
        _historyRendered = true;
        try
        {
            string? query = string.IsNullOrWhiteSpace(_historyQuery) ? null : _historyQuery;
            var rows = await _history.SearchAsync(query, 50, 0).ConfigureAwait(true);
            _historyStats.StringValue = query is null
                ? $"{rows.Count} transcript{(rows.Count == 1 ? string.Empty : "s")}"
                : $"{rows.Count} match{(rows.Count == 1 ? string.Empty : "es")}";

            _hoveredRow = null;
            foreach (var v in _historyList.ArrangedSubviews.ToArray())
            {
                _historyList.RemoveArrangedSubview(v);
                v.RemoveFromSuperview();
            }
            foreach (var r in rows)
            {
                var row = new HistoryRowView(this, r);
                _historyList.AddArrangedSubview(row);
                row.WidthAnchor.ConstraintEqualTo(_historyList.WidthAnchor).Active = true;
            }
            if (rows.Count == 0)
            {
                var empty = UI.Label(query is null ? "Nothing here yet. Your dictations will appear here." : "No transcripts match that search.", Fonts.Body, "MutedText");
                var box = Padded(empty, Settings.RowPadX, Settings.RowPadY);
                _historyList.AddArrangedSubview(box);
                box.WidthAnchor.ConstraintEqualTo(_historyList.WidthAnchor).Active = true;
            }
            _purgeButton.Enabled = rows.Count > 0 || query is not null;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logger.LogWarning(ex, "Failed to query history.");
            _historyStats.StringValue = "History unavailable.";
        }
    }

    private void OnHistorySearchChanged()
    {
        _historyDebounce?.Invalidate();
        _historyDebounce = Timers.Once(TimeSpan.FromMilliseconds(250), timer =>
        {
            _historyQuery = _historySearch?.StringValue.Trim() ?? string.Empty;
            _ = ReloadHistoryAsync();
        });
    }

    private void OnPurgeAllClick()
    {
        var alert = new NSAlert
        {
            MessageText = "Delete all transcripts?",
            InformativeText = "Every saved transcript is removed from this Mac. This can't be undone.",
            AlertStyle = NSAlertStyle.Warning,
        };
        alert.AddButton("Delete All").HasDestructiveAction = true;
        alert.AddButton("Cancel");
        alert.BeginSheet(_window, async response =>
        {
            if ((long)response != 1000) // NSAlertFirstButtonReturn
            {
                return;
            }
            try
            {
                await _history.PurgeAllAsync().ConfigureAwait(true);
                _logger.LogInformation("History purged by user.");
                await ReloadHistoryAsync().ConfigureAwait(true);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                _logger.LogWarning(ex, "PurgeAllAsync failed.");
            }
        });
    }

    private static void CopyTranscript(TranscriptRecord r)
    {
        var pb = NSPasteboard.GeneralPasteboard;
        pb.ClearContents();
        pb.SetStringForType(r.Text, NSPasteboardType.String.GetConstant()!);
    }

    private void SetHoveredRow(HistoryRowView row)
    {
        if (_hoveredRow is not null && _hoveredRow != row)
        {
            _hoveredRow.SetHover(false);
        }
        _hoveredRow = row;
        row.SetHover(true);
    }

    private void ClearHoveredRow(HistoryRowView row)
    {
        row.SetHover(false);
        if (_hoveredRow == row)
        {
            _hoveredRow = null;
        }
    }

    private void ConfirmDeleteTranscript(TranscriptRecord r)
    {
        var alert = new NSAlert
        {
            MessageText = "Delete this transcript?",
            InformativeText = r.Text.Length > 120 ? r.Text[..117] + "…" : r.Text,
            AlertStyle = NSAlertStyle.Warning,
        };
        alert.AddButton("Delete").HasDestructiveAction = true;
        alert.AddButton("Cancel");
        alert.BeginSheet(_window, response =>
        {
            if ((long)response == 1000)
            {
                _ = DeleteTranscriptAsync(r);
            }
        });
    }

    private async Task DeleteTranscriptAsync(TranscriptRecord r)
    {
        try
        {
            await _history.DeleteAsync(r.Id).ConfigureAwait(true);
            await ReloadHistoryAsync().ConfigureAwait(true);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _logger.LogWarning(ex, "DeleteAsync failed for transcript {Id}.", r.Id);
        }
    }

    /// <summary>
    /// History row: failure icon / time / app / transcript / model / length. Copy and Delete
    /// replace the model + length cells on hover or keyboard focus (the row is focusable:
    /// Tab to it, ⌘C copies, Delete asks to remove). Right-click offers the same actions.
    /// </summary>
    private sealed class HistoryRowView : FlippedView
    {
        private readonly PreferencesWindowController _owner;
        private readonly TranscriptRecord _record;
        private readonly NSView _model;
        private readonly NSView _duration;
        private readonly NSStackView _actions;
        private NSTrackingArea? _tracking;
        private bool _hover;
        private bool _focused;

        public HistoryRowView(PreferencesWindowController owner, TranscriptRecord r)
        {
            _owner = owner;
            _record = r;
            TranslatesAutoresizingMaskIntoConstraints = false;
            FocusRingType = NSFocusRingType.Default;
            bool ok = r.Status == TranscriptStatus.Ok;

            NSView status = new NSView();
            if (!ok)
            {
                var icon = UI.SymbolView("xmark.octagon.fill", 12, "ErrorText");
                icon.AccessibilityElement = false;
                status = icon;
            }
            var time = UI.Label(UI.FormatRelative(r.Timestamp), Fonts.SupportingNumeric, "MutedText");
            time.ToolTip = r.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            var app = UI.Label(string.IsNullOrEmpty(r.TargetApp) ? "—" : r.TargetApp, Fonts.Body, "SecondaryText");
            app.ToolTip = r.TargetApp ?? string.Empty;
            // The icon already says "failed"; don't repeat it in the text (display only).
            var shown = !ok && r.Text.StartsWith("[failed]", StringComparison.OrdinalIgnoreCase) ? r.Text[8..].TrimStart() : r.Text;
            var transcript = UI.Label(shown, Fonts.Body, ok ? "PrimaryText" : "ErrorText");
            transcript.ToolTip = r.Text;
            _model = UI.Label(UI.ShortModelId(r.Model), Fonts.Supporting, "MutedText");
            _model.ToolTip = r.Model;
            _duration = RightAligned(UI.Label($"{r.Duration.TotalSeconds:F1}s", Fonts.SupportingNumeric, "MutedText"));

            var copy = RowAction(UI.IconButton("doc.on.doc", "Copy text", () => CopyTranscript(r), "SecondaryText", 12));
            Settings.AxLabel(copy, "Copy text");
            var delete = RowAction(UI.IconButton("trash", "Delete", () => owner.ConfirmDeleteTranscript(r), "ErrorText", 12));
            Settings.AxLabel(delete, "Delete transcript");
            _actions = UI.HStack(4, UI.Spacer(), copy, delete);
            _actions.Hidden = true;
            _actions.WidthAnchor.ConstraintEqualTo(88 + 12 + 48).Active = true;

            var grid = HistoryGrid(status, time, app, transcript, _model, _duration);
            grid.AddArrangedSubview(_actions);
            // Fixed content height: revealing the 24 pt actions must not change the row height.
            grid.HeightAnchor.ConstraintEqualTo(24).Active = true;
            var padded = Padded(grid, Settings.RowPadX, 8);
            var line = Hairline();
            var column = UI.VStack(0, line, padded);
            padded.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
            // Same separator as every settings group: inset 16 pt on both sides.
            line.WidthAnchor.ConstraintEqualTo(column.WidthAnchor, 1, -(nfloat)(2 * Settings.RowPadX)).Active = true;
            column.Alignment = NSLayoutAttribute.CenterX;
            AddSubview(column);
            NSLayoutConstraint.ActivateConstraints(
            [
                column.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                column.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
                column.TopAnchor.ConstraintEqualTo(TopAnchor),
                column.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            ]);

            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.RowRole;
            AccessibilityLabel = $"{(ok ? string.Empty : "Failed. ")}{r.Text}. {UI.FormatRelative(r.Timestamp)}{(string.IsNullOrEmpty(r.TargetApp) ? string.Empty : ", in " + r.TargetApp)}";

            var menu = new NSMenu();
            menu.AddItem(new NSMenuItem("Copy Text", (_, _) => CopyTranscript(r)));
            menu.AddItem(new NSMenuItem("Delete…", (_, _) => owner.ConfirmDeleteTranscript(r)));
            Menu = menu;
        }

        public override bool AcceptsFirstResponder() => true;

        public override bool CanBecomeKeyView => true;

        public override CGRect FocusRingMaskBounds => Bounds;

        public override void DrawFocusRingMask() => NSBezierPath.FillRect(Bounds);

        public override bool BecomeFirstResponder()
        {
            _focused = true;
            Paint();
            return true;
        }

        public override bool ResignFirstResponder()
        {
            _focused = false;
            Paint();
            return true;
        }

        public override void KeyDown(NSEvent theEvent)
        {
            bool cmd = (theEvent.ModifierFlags & NSEventModifierMask.CommandKeyMask) != 0;
            if (cmd && theEvent.CharactersIgnoringModifiers == "c")
            {
                CopyTranscript(_record);
                return;
            }
            if (theEvent.KeyCode is 51 or 117) // Delete / Forward Delete
            {
                _owner.ConfirmDeleteTranscript(_record);
                return;
            }
            base.KeyDown(theEvent);
        }

        public override void UpdateTrackingAreas()
        {
            base.UpdateTrackingAreas();
            if (_tracking is not null)
            {
                RemoveTrackingArea(_tracking);
            }
            _tracking = new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null);
            AddTrackingArea(_tracking);
            // Rebuilding the area (scroll, layout) drops the enter/exit for the old bounds;
            // re-derive hover from where the cursor actually is.
            if (Window is { } w)
            {
                var p = ConvertPointFromView(w.MouseLocationOutsideOfEventStream, null);
                bool inside = Bounds.Contains(p);
                if (inside != _hover)
                {
                    if (inside)
                    {
                        _owner.SetHoveredRow(this);
                    }
                    else
                    {
                        _owner.ClearHoveredRow(this);
                    }
                }
            }
        }

        public override void MouseEntered(NSEvent theEvent) => _owner.SetHoveredRow(this);

        public override void MouseExited(NSEvent theEvent) => _owner.ClearHoveredRow(this);

        public void SetHover(bool hover)
        {
            if (_hover == hover)
            {
                return;
            }
            _hover = hover;
            Paint();
        }

        private static NSButton RowAction(NSButton b)
        {
            foreach (var c in b.Constraints.ToArray())
            {
                if (c.FirstItem == b && c.SecondItem is null)
                {
                    b.RemoveConstraint(c);
                }
            }
            b.WidthAnchor.ConstraintEqualTo(24).Active = true;
            b.HeightAnchor.ConstraintEqualTo(24).Active = true;
            return b;
        }

        private void Paint()
        {
            bool active = _hover || _focused;
            Layer!.BackgroundColor = active ? ThemeColors.Cg("HoverSubtle") : NSColor.Clear.CGColor;
            _model.Hidden = active;
            _duration.Hidden = active;
            _actions.Hidden = !active;
        }
    }

    // ── Privacy ─────────────────────────────────────────────────────────────

    private NSTextField? _offlineSubtitle;
    private NSSwitch? _crashToggle;
    private NSTextField? _crashSubtitle;
    private NSTextField? _logsSize;

    private NSStackView BuildPrivacyTab()
    {
        var s = _prefs.Current.Privacy;

        var intro = Settings.Intro("Your audio, transcripts and history stay on this Mac.",
            UI.Label("KusPus only goes online to download models and, if you allow it, to send crash reports.", Fonts.Supporting, "MutedText", wraps: true));

        var offlineToggle = UI.Toggle(s.OfflineMode, on => _ = SavePrivacyAsync(p => p with { OfflineMode = on }, "OfflineMode"));
        var offlineRow = new SettingsRow("Offline mode", OfflineSubtitleText(s.OfflineMode), offlineToggle);
        _offlineSubtitle = offlineRow.Secondary;

        _crashToggle = UI.Toggle(s.CrashReportsOptIn, on => _ = SavePrivacyAsync(p => p with { CrashReportsOptIn = on }, "CrashReportsOptIn"));
        var crashRow = new SettingsRow("Share crash reports", " ", _crashToggle);
        _crashSubtitle = crashRow.Secondary;
        ApplyCrashReportsGating(s.OfflineMode);

        _logsSize = UI.Label("Calculating…", Fonts.SupportingNumeric, "MutedText");
        var logsRow = new SettingsRow(UI.Label("Log files", Fonts.RowTitle, "PrimaryText"), _logsSize,
            Settings.Destructive("Clear…", OnClearLogsClick), "Log files");
        var logsPath = UI.Label(MacPaths.LogsDir, Fonts.Supporting, "MutedText");
        logsPath.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        logsPath.ToolTip = MacPaths.LogsDir;
        var folderRow = new SettingsRow(UI.Label("Log folder", Fonts.RowTitle, "PrimaryText"), logsPath,
            UI.Button("Show in Finder", UI.ButtonKind.Secondary, RevealLogs), "Log folder");
        RefreshLogsSize();

        return Tab(
            intro,
            Settings.Section("Network", Settings.Group(offlineRow, crashRow)),
            Settings.Section("Logs", Settings.Group(logsRow, folderRow)));
    }

    private static string OfflineSubtitleText(bool enabled) => enabled
        ? "On. KusPus makes no network requests; model downloads are paused."
        : "Off. Turn on to block every network request.";

    private async Task SavePrivacyAsync(Func<Core.Settings.PrivacySettings, Core.Settings.PrivacySettings> mutate, string field)
    {
        var current = _prefs.Current;
        var next = current with { Privacy = mutate(current.Privacy) };
        if (next.Privacy == current.Privacy)
        {
            return;
        }
        _logger.LogInformation("Privacy.{Field} changed.", field);
        if (_offlineSubtitle is not null)
        {
            _offlineSubtitle.StringValue = OfflineSubtitleText(next.Privacy.OfflineMode);
        }
        try
        {
            await _prefs.UpdateAsync(s => s with { Privacy = mutate(s.Privacy) }).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for {Field}.", field);
        }
    }

    // §13.1 — Crash Reports is disabled while Offline Mode is on; opt-in state is preserved.
    private void ApplyCrashReportsGating(bool offlineModeOn)
    {
        if (_crashToggle is null || _crashSubtitle is null)
        {
            return;
        }
        _crashToggle.Enabled = !offlineModeOn;
        _crashSubtitle.StringValue = offlineModeOn
            ? "Unavailable while offline mode is on."
            : "Anonymous. Never includes your audio, transcripts or clipboard.";
    }

    private void RefreshLogsSize()
    {
        if (_logsSize is null)
        {
            return;
        }
        try
        {
            long total = 0;
            if (Directory.Exists(MacPaths.LogsDir))
            {
                foreach (var file in Directory.EnumerateFiles(MacPaths.LogsDir, "*.log"))
                {
                    try { total += new FileInfo(file).Length; } catch (FileNotFoundException) { }
                }
            }
            _logsSize.StringValue = UI.FormatBytes(total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to enumerate logs directory.");
            _logsSize.StringValue = "Unavailable";
        }
    }

    private void OnClearLogsClick()
    {
        var alert = new NSAlert
        {
            MessageText = "Clear log files?",
            InformativeText = "All KusPus log files are deleted. A new log starts automatically.",
            AlertStyle = NSAlertStyle.Warning,
        };
        alert.AddButton("Clear").HasDestructiveAction = true;
        alert.AddButton("Cancel");
        alert.BeginSheet(_window, response =>
        {
            if ((long)response != 1000)
            {
                return;
            }
            int deleted = 0, skipped = 0;
            void DeleteAll()
            {
                if (!Directory.Exists(MacPaths.LogsDir))
                {
                    return;
                }
                foreach (var file in Directory.EnumerateFiles(MacPaths.LogsDir, "*.log"))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
            }
            // Close the open log file first so a genuinely new one starts afterwards.
            if (AppDelegate.LogFileSink is { } sink)
            {
                sink.Restart(DeleteAll);
            }
            else
            {
                DeleteAll();
            }
            _logger.LogInformation("Clear logs: deleted={Deleted} skipped={Skipped}.", deleted, skipped);
            RefreshLogsSize();
        });
    }

    private static void RevealLogs()
    {
        Directory.CreateDirectory(MacPaths.LogsDir);
        NSWorkspace.SharedWorkspace.SelectFile(MacPaths.LogsDir, string.Empty);
    }

    // ── About ───────────────────────────────────────────────────────────────

    private NSStackView BuildAboutTab()
    {
        var icon = NSImageView.FromImage(NSImage.ImageNamed(NSImageName.ApplicationIcon) ?? new NSImage());
        icon.TranslatesAutoresizingMaskIntoConstraints = false;
        icon.ImageScaling = NSImageScale.ProportionallyUpOrDown;
        icon.WidthAnchor.ConstraintEqualTo(64).Active = true;
        icon.HeightAnchor.ConstraintEqualTo(64).Active = true;
        icon.AccessibilityElement = false;

        var version = NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]?.ToString() ?? "dev";
        var exe = Environment.ProcessPath;
        var built = exe is not null && File.Exists(exe)
            ? File.GetLastWriteTime(exe).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : "—";
        var title = UI.Label("KusPus", Fonts.Semibold(22), "PrimaryText");
        var tagline = UI.Label("Your words. Your Mac. No one else.", Fonts.Body, "SecondaryText");
        var meta = UI.Label($"Version {version} · {built}", Fonts.SupportingNumeric, "MutedText");
        var text = UI.VStack(4, title, tagline, meta);
        var header = UI.VStack(0, UI.HStack(16, icon, text));
        header.EdgeInsets = new NSEdgeInsets(0, 0, (nfloat)Settings.SectionGap, 0);

        var sourceRow = new SettingsRow("Source code", "github.com/devangk003/kuspus",
            UI.Button("Open on GitHub", UI.ButtonKind.Secondary, () => UI.OpenUrl("https://github.com/devangk003/kuspus")));
        var onboardingRow = new SettingsRow("Setup guide", "Walk through the first-launch steps again.",
            UI.Button("Run again", UI.ButtonKind.Secondary, () => _ = _rerunOnboarding()));

        var footerLeft = UI.Label("MIT licence · Runs on your Mac", Fonts.Supporting, "MutedText");
        var links = UI.HStack(8,
            SocialButton("linkedin.svg", "LinkedIn", "https://www.linkedin.com/in/devangk003/"),
            SocialButton("x.svg", "X", "https://x.com/devang_kumawat"),
            SocialButton("github.svg", "GitHub", "https://github.com/devangk003"),
            SocialButton("globe.svg", "Website", "https://lnk.bio/devangk003"));
        var byline = UI.Label("Made by Devang Kumawat", Fonts.Supporting, "SecondaryText");
        var footerRight = UI.HStack(12, byline, links);
        var footer = UI.Row(footerLeft, footerRight);

        return Tab(
            header,
            Settings.Section("Resources", Settings.Group(sourceRow, onboardingRow)),
            footer);
    }

    private static NSButton SocialButton(string svg, string tooltip, string url)
    {
        var image = UI.BundledImage(svg);
        var button = image is not null
            ? NSButton.CreateButton(image, () => UI.OpenUrl(url))
            : NSButton.CreateButton(UI.Symbol("link", 13), () => UI.OpenUrl(url));
        if (image is not null)
        {
            image.Template = true;
            image.Size = new CGSize(14, 14);
        }
        button.Bordered = false;
        button.Title = string.Empty;
        button.ImagePosition = NSCellImagePosition.ImageOnly;
        button.ContentTintColor = ThemeColors.Get("SecondaryText");
        button.ToolTip = tooltip;
        Settings.AxLabel(button, $"{tooltip}, opens in browser");
        button.TranslatesAutoresizingMaskIntoConstraints = false;
        UI.Width(button, 28);
        UI.Height(button, 28);
        return button;
    }
}
