using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GCS.Core.Advisor;
using GCS.Core.Advisor.Ai;
using GCS.Core.Domain;

namespace GCS.ViewModels;

/// <summary>
/// One row in the advisor's component list.
/// </summary>
public sealed class AdvisorComponentViewModel : ViewModelBase
{
    public string Name { get; }
    public string Status { get; }
    public string Summary { get; }
    public string ScoreText { get; }
    public bool IsMeasured { get; }
    public string Findings { get; }

    /// <summary>Colour key resolved by the view; kept as a string so Core stays UI-free.</summary>
    public string StatusKey { get; }

    public AdvisorComponentViewModel(ComponentHealth c)
    {
        Name = c.Name;
        IsMeasured = c.IsMeasured;
        Summary = c.Summary;

        // An unmeasured component shows a dash, never "0%" — the whole point of
        // the rewrite is that unknown and bad are different things.
        ScoreText = c.Score is null ? "—" : $"{c.Score}%";

        (Status, StatusKey) = c.Status switch
        {
            ComponentStatus.Ok => ("OK", "Ok"),
            ComponentStatus.Warning => ("WARNING", "Warning"),
            ComponentStatus.Critical => ("CRITICAL", "Critical"),
            _ => ("NOT MONITORED", "NoData"),
        };

        Findings = string.Join(Environment.NewLine, c.Evidence.Select(e => "• " + e.Text));
    }
}

/// <summary>
/// Drives the flight advisor tab. All judgement lives in
/// <see cref="FlightHealthAnalyzer"/>; this class only turns a report into
/// bindable rows and keeps the battery trend across updates.
/// </summary>
/// <summary>One line of the assistant conversation.</summary>
public sealed class ChatMessageViewModel
{
    public string Sender { get; init; } = "";
    public string Text { get; init; } = "";
    public bool IsFromOperator { get; init; }
    public string Time { get; init; } = "";
}

public sealed class AdvisorViewModel : ViewModelBase
{
    private readonly BatteryTrend _batteryTrend = new();

    /// <summary>Latest report and state, so a question is answered from live data.</summary>
    private FlightHealthReport? _lastReport;
    private VehicleState? _lastState;

    /// <summary>Stand-in used before any telemetry arrives — everything unmeasured.</summary>
    private static readonly VehicleState DisconnectedState =
        new(null, null, null, null, null, null, null, false);

    /// <summary>
    /// A recorded flight under review. While set, questions are answered about that
    /// flight rather than the live aircraft — asking "how was the battery?" during a
    /// log review should describe the log, not the vehicle on the bench.
    /// </summary>
    private GCS.Core.Logging.FlightLogSummary? _logContext;

    /// <summary>
    /// Pulled fresh at question time rather than cached: parameters are loaded on
    /// demand and edited during a session, so a snapshot taken at startup would be
    /// empty and one taken at connect would go stale.
    /// </summary>
    public Func<ParameterSnapshot>? ParameterProvider { get; set; }
    public Func<SetupSnapshot>? SetupProvider { get; set; }

    public bool HasLogContext => _logContext != null;

    public string LogContextText => _logContext is null
        ? ""
        : $"Reviewing {_logContext.FileName}";

    public RelayCommand ClearLogContextCommand { get; }

    /// <summary>Open the assistant against a recorded flight.</summary>
    public void ReviewLog(GCS.Core.Logging.FlightLogSummary summary)
    {
        _logContext = summary;
        OnPropertyChanged(nameof(HasLogContext));
        OnPropertyChanged(nameof(LogContextText));

        Append("Advisor",
            $"Reviewing {summary.FileName} — {summary.DurationText}, " +
            $"{summary.PacketCount:N0} packets. Ask me what happened.",
            fromOperator: false);

        IsChatOpen = true;
    }

    /// <summary>Analysis is cheap but runs on every telemetry frame; throttle it.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);
    private DateTime _lastRun = DateTime.MinValue;

    public ObservableCollection<AdvisorComponentViewModel> Components { get; } = new();

    // ── Assistant ───────────────────────────────────────────────────

    public ObservableCollection<ChatMessageViewModel> Conversation { get; } = new();

    private string _question = "";
    public string Question
    {
        get => _question;
        set => SetProperty(ref _question, value);
    }

    // CanExecute is re-queried by CommandManager.RequerySuggested, so typing in the
    // box enables the button without the ViewModel pushing an update.
    public RelayCommand AskCommand { get; }
    public RelayCommand ToggleChatCommand { get; }
    public RelayCommand CloseChatCommand { get; }
    public RelayCommand ClearChatCommand { get; }

    // ── Assistant settings ──────────────────────────────────────────

    private AssistantOptions _options;

    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CloseSettingsCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand TestConnectionCommand { get; }
    public RelayCommand LoadModelsCommand { get; }
    public RelayCommand AutoDetectModelCommand { get; }

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (!SetProperty(ref _isSettingsOpen, value)) return;
            OnPropertyChanged(nameof(IsChatViewVisible));

            if (!value) return;

            SettingsStatus = "";

            // The fetched list is not persisted, so seed it with the saved model —
            // otherwise the editable combo opens blank and it looks like nothing
            // is configured.
            if (!string.IsNullOrWhiteSpace(CustomModel) && !AvailableModels.Contains(CustomModel))
                AvailableModels.Insert(0, CustomModel);
        }
    }

    /// <summary>The transcript hides while settings are showing — one panel, two faces.</summary>
    public bool IsChatViewVisible => !_isSettingsOpen;

    public IReadOnlyList<AssistantProviderInfo> Providers => AssistantProviders.All;

    private AssistantProviderInfo _selectedProvider;
    public AssistantProviderInfo SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!SetProperty(ref _selectedProvider, value)) return;
            OnPropertyChanged(nameof(InstructionText));
            OnPropertyChanged(nameof(KeyUrl));
            OnPropertyChanged(nameof(HasKeyUrl));
            OnPropertyChanged(nameof(NeedsKey));
            OnPropertyChanged(nameof(NeedsBaseUrl));
            OnPropertyChanged(nameof(EffectiveModelText));
        }
    }

    private string _apiKey;
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }

    private string _customBaseUrl;
    public string CustomBaseUrl { get => _customBaseUrl; set => SetProperty(ref _customBaseUrl, value); }

    private string _customModel;
    public string CustomModel
    {
        get => _customModel;
        set
        {
            if (SetProperty(ref _customModel, value))
                OnPropertyChanged(nameof(EffectiveModelText));
        }
    }

    /// <summary>
    /// Which model will actually be used — the typed one, or the provider default
    /// when blank. Shown because an empty box gave no clue what was in effect.
    /// </summary>
    public string EffectiveModelText
    {
        get
        {
            if (!SelectedProvider.RequiresKey) return "";

            string effective = string.IsNullOrWhiteSpace(CustomModel)
                ? SelectedProvider.DefaultModel
                : CustomModel.Trim();

            return string.IsNullOrWhiteSpace(effective)
                ? "No model set — use 'Find one that works'."
                : $"Will use: {effective}";
        }
    }

    /// <summary>
    /// Models the key can actually use, fetched from the provider. Empty until
    /// asked — model names and free-tier eligibility change over time, so this is
    /// more reliable than any list shipped with the app.
    /// </summary>
    public ObservableCollection<string> AvailableModels { get; } = new();

    public bool HasAvailableModels => AvailableModels.Count > 0;

    private string _settingsStatus = "";
    public string SettingsStatus { get => _settingsStatus; private set => SetProperty(ref _settingsStatus, value); }

    private bool _settingsStatusIsError;
    public bool SettingsStatusIsError { get => _settingsStatusIsError; private set => SetProperty(ref _settingsStatusIsError, value); }

    private bool _isTesting;
    public bool IsTesting { get => _isTesting; private set => SetProperty(ref _isTesting, value); }

    public string InstructionText =>
        string.Join(Environment.NewLine,
            SelectedProvider.Steps.Select((s, i) => $"{i + 1}. {s}"));

    public string KeyUrl => SelectedProvider.KeyUrl;
    public bool HasKeyUrl => !string.IsNullOrWhiteSpace(SelectedProvider.KeyUrl);
    public bool NeedsKey => SelectedProvider.RequiresKey;
    public bool NeedsBaseUrl => SelectedProvider.RequiresBaseUrl;


    private AssistantOptions BuildOptions() => new()
    {
        Provider = SelectedProvider.Id,
        ApiKey = ApiKey?.Trim() ?? "",
        BaseUrl = CustomBaseUrl?.Trim() ?? "",
        Model = CustomModel?.Trim() ?? "",
        TimeoutSeconds = _options.TimeoutSeconds,
    };

    private void SaveSettings()
    {
        var options = BuildOptions();

        if (options.IsConfigured || !SelectedProvider.RequiresKey)
        {
            if (!GCS.Core.AppConfig.SaveAssistant(options, out string? error))
            {
                SetStatus($"Could not save: {error}", isError: true);
                return;
            }

            _options = options;
            ApplyOptions(options);
            SetStatus(options.IsConfigured
                ? $"Saved. Using {options.DisplayName}."
                : "Saved. Using built-in answers.", isError: false);
            return;
        }

        // Tell them exactly which field is missing rather than a generic failure.
        SetStatus(string.IsNullOrWhiteSpace(options.ApiKey)
            ? "Enter an API key first."
            : "Enter the base URL and model for a custom provider.", isError: true);
    }

    /// <summary>Swap in a client for the new settings without restarting the app.</summary>
    private void ApplyOptions(AssistantOptions options)
    {
        _assistant = new AssistantService(
            options.IsConfigured ? new OpenAiCompatibleChatClient(options) : null);

        OnPropertyChanged(nameof(ProviderText));
    }

    private async void LoadModels()
    {
        var options = BuildOptions();

        if (string.IsNullOrWhiteSpace(options.ApiKey) || !SelectedProvider.RequiresKey)
        {
            SetStatus("Enter an API key first.", isError: true);
            return;
        }

        IsTesting = true;
        SetStatus("Loading models…", isError: false);

        try
        {
            using var client = new OpenAiCompatibleChatClient(options);
            var (success, models, error) = await client.ListModelsAsync().ConfigureAwait(true);

            if (!success)
            {
                SetStatus(error ?? "Could not load models.", isError: true);
                return;
            }

            // Providers list embeddings, image, TTS and moderation models alongside
            // chat ones; none of those can answer a question, so offering them just
            // invites picking one that cannot work.
            PopulateChatModels(models);

            SetStatus(AvailableModels.Count == 0
                ? "This key offers no chat-capable models."
                : $"{AvailableModels.Count} chat models available. Pick one, then Test.",
                isError: AvailableModels.Count == 0);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load models: {ex.Message}", isError: true);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// Find a model this key can actually use, by trying them.
    ///
    /// Appearing in the provider's model list does not mean the key may call it —
    /// on a free tier most models answer 429 with no quota. The only reliable test
    /// is a real request, so this walks the ranked candidates until one succeeds.
    /// </summary>
    private async void AutoDetectModel()
    {
        var options = BuildOptions();

        if (string.IsNullOrWhiteSpace(options.ApiKey) || !SelectedProvider.RequiresKey)
        {
            SetStatus("Enter an API key first.", isError: true);
            return;
        }

        IsTesting = true;

        try
        {
            using var client = new OpenAiCompatibleChatClient(options);

            SetStatus("Listing models…", isError: false);
            var (listed, models, listError) = await client.ListModelsAsync().ConfigureAwait(true);

            if (!listed)
            {
                SetStatus(listError ?? "Could not list models.", isError: true);
                return;
            }

            PopulateChatModels(models);

            var candidates = AvailableModels.Take(MaxModelsToProbe).ToList();
            if (candidates.Count == 0)
            {
                SetStatus("No chat-capable models were offered for this key.", isError: true);
                return;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string model = candidates[i];
                SetStatus($"Trying {model} ({i + 1}/{candidates.Count})…", isError: false);

                var reply = await client.ProbeModelAsync(model).ConfigureAwait(true);

                if (reply.Success)
                {
                    CustomModel = model;
                    SetStatus($"Found a working model: {model}. Press Save to use it.", isError: false);
                    return;
                }

                // A rejected key will reject every model — stop rather than grind
                // through the whole list.
                if (reply.Error is { } err &&
                    err.Contains("key was rejected", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(err, isError: true);
                    return;
                }
            }

            SetStatus($"Tried {candidates.Count} models, none available on this key. " +
                      "The account may have no free quota — try another provider.", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Auto-detect failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Bounded so a long model list cannot turn into a minutes-long stall.</summary>
    private const int MaxModelsToProbe = 8;

    /// <summary>
    /// Fill the picker with chat models only, cheapest-first — the order that is
    /// most likely to be permitted on a free tier, so the top entry is the best
    /// first guess as well as the first one auto-detect tries.
    /// </summary>
    private void PopulateChatModels(IEnumerable<string> models)
    {
        AvailableModels.Clear();
        foreach (var m in ModelCandidates.RankChatModels(models))
            AvailableModels.Add(m);

        OnPropertyChanged(nameof(HasAvailableModels));
    }

    private async void TestConnection()
    {
        var options = BuildOptions();

        if (!options.IsConfigured)
        {
            SetStatus(SelectedProvider.RequiresKey
                ? "Enter an API key first."
                : "Nothing to test — built-in answers need no connection.", isError: true);
            return;
        }

        IsTesting = true;
        SetStatus("Testing…", isError: false);

        try
        {
            using var client = new OpenAiCompatibleChatClient(options);
            var reply = await client
                .AskAsync("You are a test. Reply with the single word: OK.", "Reply with OK.")
                .ConfigureAwait(true);

            SetStatus(reply.Success
                ? $"Connected to {options.DisplayName}."
                : reply.Error ?? "The provider did not respond.", isError: !reply.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Test failed: {ex.Message}", isError: true);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void SetStatus(string text, bool isError)
    {
        SettingsStatus = text;
        SettingsStatusIsError = isError;
    }

    private bool _isChatOpen;
    public bool IsChatOpen
    {
        get => _isChatOpen;
        private set
        {
            if (!SetProperty(ref _isChatOpen, value)) return;
            OnPropertyChanged(nameof(IsChatClosed));
            if (value) HasUnread = false;   // opening is acknowledgement
        }
    }

    /// <summary>The launcher button shows only while the panel is closed.</summary>
    public bool IsChatClosed => !_isChatOpen;

    private bool _hasUnread;
    /// <summary>Drives the dot on the launcher when the advisor spoke unprompted.</summary>
    public bool HasUnread { get => _hasUnread; private set => SetProperty(ref _hasUnread, value); }

    private AssistantService _assistant;

    private bool _isThinking;
    public bool IsThinking { get => _isThinking; private set => SetProperty(ref _isThinking, value); }

    /// <summary>Which engine is answering, shown in the panel header.</summary>
    public string ProviderText => _assistant.HasModel
        ? $"AI: {_assistant.ProviderName}"
        : "Built-in answers";

    public AdvisorViewModel(AssistantService? assistant = null, AssistantOptions? options = null)
    {
        _assistant = assistant ?? new AssistantService();

        _options = options ?? new AssistantOptions();
        _selectedProvider = AssistantProviders.Find(_options.Provider);
        _apiKey = _options.ApiKey;
        _customBaseUrl = _options.BaseUrl;
        _customModel = _options.Model;

        AskCommand = new RelayCommand(
            Ask,
            () => !string.IsNullOrWhiteSpace(Question) && !IsThinking);

        OpenSettingsCommand = new RelayCommand(() => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(() => IsSettingsOpen = false);
        SaveSettingsCommand = new RelayCommand(SaveSettings, () => !IsTesting);
        TestConnectionCommand = new RelayCommand(TestConnection, () => !IsTesting);
        LoadModelsCommand = new RelayCommand(LoadModels, () => !IsTesting);
        AutoDetectModelCommand = new RelayCommand(AutoDetectModel, () => !IsTesting);

        ToggleChatCommand = new RelayCommand(() => IsChatOpen = !IsChatOpen);
        CloseChatCommand = new RelayCommand(() => IsChatOpen = false);
        ClearChatCommand = new RelayCommand(Conversation.Clear, () => Conversation.Count > 0);

        ClearLogContextCommand = new RelayCommand(() =>
        {
            _logContext = null;
            OnPropertyChanged(nameof(HasLogContext));
            OnPropertyChanged(nameof(LogContextText));
            Append("Advisor", "Back to the live aircraft.", fromOperator: false);
        });
    }

    private async void Ask()
    {
        string question = Question.Trim();
        if (question.Length == 0 || IsThinking) return;

        Append("You", question, fromOperator: true);
        Question = "";

        // Snapshot now: telemetry keeps arriving while the model is thinking, and
        // the answer must describe the aircraft as it was when asked.
        //
        // With nothing connected an empty snapshot is used rather than refusing:
        // plenty of useful questions ("what can you tell me?", "how do I arm?")
        // have nothing to do with live telemetry, and the analyzer already reports
        // every subsystem as unmeasured, so nothing can be fabricated.
        var state = _lastState ?? DisconnectedState;
        var report = _lastReport ?? FlightHealthAnalyzer.Analyze(state, DateTime.UtcNow);

        IsThinking = true;
        try
        {
            var parameters = ParameterProvider?.Invoke();
            var setup = SetupProvider?.Invoke();

            var answer = await _assistant
                .AnswerAsync(question, report, state, DateTime.UtcNow, default,
                             _logContext, parameters, setup)
                .ConfigureAwait(true);

            Append("Advisor", answer.Text, false);

            // Say when the model was unreachable rather than silently downgrading —
            // the operator should know which engine answered.
            if (answer.Source == AnswerSource.ModelFailedFellBack)
                Append("Advisor", $"({answer.Note} Answered from built-in rules.)", false);
        }
        catch (Exception ex)
        {
            Append("Advisor", $"Assistant failed: {ex.Message}", false);
        }
        finally
        {
            IsThinking = false;
        }
    }

    private void Append(string sender, string text, bool fromOperator)
    {
        Conversation.Add(new ChatMessageViewModel
        {
            Sender = sender,
            Text = text,
            IsFromOperator = fromOperator,
            Time = DateTime.Now.ToString("HH:mm:ss"),
        });

        // Keep the transcript bounded on a long flight.
        while (Conversation.Count > 200) Conversation.RemoveAt(0);

        // Only the advisor speaking while the panel is shut is "unread".
        if (!fromOperator && !IsChatOpen) HasUnread = true;
    }

    private string _headline = "Waiting for telemetry";
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    private string _overallScoreText = "—";
    public string OverallScoreText { get => _overallScoreText; private set => SetProperty(ref _overallScoreText, value); }

    private string _verdictText = "NO DATA";
    public string VerdictText { get => _verdictText; private set => SetProperty(ref _verdictText, value); }

    private string _verdictKey = "NoData";
    public string VerdictKey { get => _verdictKey; private set => SetProperty(ref _verdictKey, value); }

    private string _coverageText = "";
    public string CoverageText { get => _coverageText; private set => SetProperty(ref _coverageText, value); }

    private string _batteryTrendText = "";
    public string BatteryTrendText { get => _batteryTrendText; private set => SetProperty(ref _batteryTrendText, value); }

    public void Reset()
    {
        _batteryTrend.Reset();
        _lastReport = null;
        _lastState = null;
        HasUnread = false;
        Components.Clear();
        Headline = "Waiting for telemetry";
        OverallScoreText = "—";
        VerdictText = "NO DATA";
        VerdictKey = "NoData";
        CoverageText = "";
        BatteryTrendText = "";
    }

    public void UpdateFromVehicleState(VehicleState state)
    {
        var now = DateTime.UtcNow;

        if (state.Battery is { VoltageVolts: > 0 } battery)
            _batteryTrend.Add(battery.TimestampUtc, battery.VoltageVolts);

        if (now - _lastRun < MinInterval) return;
        _lastRun = now;

        var report = FlightHealthAnalyzer.Analyze(state, now, _batteryTrend);
        _lastReport = report;
        _lastState = state;

        Headline = report.Headline;
        OverallScoreText = report.OverallScore is null ? "—" : $"{report.OverallScore}%";
        CoverageText = $"{report.Measured.Count()} of {report.Components.Count} subsystems monitored";

        // Reports observations rather than granting a clearance — the GCS sees only
        // part of the aircraft, so "no issues found" is the strongest honest claim.
        (VerdictText, VerdictKey) = report.Verdict switch
        {
            AdvisoryVerdict.CriticalIssue => ("CRITICAL ISSUE", "Critical"),
            AdvisoryVerdict.Issues => ("ISSUES FOUND", "Warning"),
            AdvisoryVerdict.LimitedData => ("NO ISSUES — LIMITED DATA", "Warning"),
            AdvisoryVerdict.NoIssues => ("NO ISSUES FOUND", "Ok"),
            _ => ("NO DATA", "NoData"),
        };

        BatteryTrendText = _batteryTrend.HasEnoughData
            ? $"{_batteryTrend.SlopeVoltsPerMinute:+0.00;-0.00} V/min"
            : "collecting…";

        RebuildComponents(report);
    }

    /// <summary>
    /// Replace rows in place where possible so the list does not flicker at 2 Hz.
    /// </summary>
    private void RebuildComponents(FlightHealthReport report)
    {
        var rows = report.Components
            .OrderByDescending(c => c.Status)      // problems first, NoData last
            .ThenBy(c => c.Name)
            .Select(c => new AdvisorComponentViewModel(c))
            .ToList();

        if (Components.Count != rows.Count)
        {
            Components.Clear();
            foreach (var row in rows) Components.Add(row);
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var existing = Components[i];
            if (existing.Name != rows[i].Name ||
                existing.Status != rows[i].Status ||
                existing.Summary != rows[i].Summary ||
                existing.ScoreText != rows[i].ScoreText ||
                existing.Findings != rows[i].Findings)
            {
                Components[i] = rows[i];
            }
        }
    }
}
