using SimpleShadowsocks.Client.Maui.Services;
using SimpleShadowsocks.Client.Socks5;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui;

public sealed class MainPage : ContentPage
{
    private readonly ProxyRunner _proxyRunner;
    private readonly UiConfigStore _configStore;
    private readonly Entry _listenPortEntry;
    private readonly Entry _remoteHostEntry;
    private readonly Entry _remotePortEntry;
    private readonly Entry _sharedKeyEntry;
    private readonly VerticalStackLayout _routingRulesContainer;
    private readonly Button _addRoutingRuleButton;
    private readonly Switch _socks5AuthenticationSwitch;
    private readonly Entry _socks5UsernameEntry;
    private readonly Entry _socks5PasswordEntry;
    private readonly Switch _compressionSwitch;
    private readonly Picker _compressionPicker;
    private readonly Picker _cipherPicker;
    private readonly View _socks5UsernameRow;
    private readonly View _socks5PasswordRow;
    private readonly Label _statusLabel;
    private readonly Editor _logEditor;
    private readonly Button _copyLogsButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly List<RoutingRuleEditorRow> _routingRuleRows = [];

    public MainPage(ProxyRunner proxyRunner, UiConfigStore configStore)
    {
        _proxyRunner = proxyRunner;
        _configStore = configStore;
        _proxyRunner.StatusChanged += OnStatusChanged;

        Title = "SimpleShadowsocks";
        var defaults = ProxyOptions.CreateDefaults();
        var saved = _configStore.Load();
        var selectedCompression = saved.ResolveCompressionAlgorithm(defaults.CompressionAlgorithm);
        var selectedCipher = saved.ResolveCipherAlgorithm(defaults.TunnelCipherAlgorithm);

        _listenPortEntry = new Entry { Keyboard = Keyboard.Numeric, Text = saved.ListenPortText };
        _remoteHostEntry = new Entry { Keyboard = Keyboard.Text, Text = saved.RemoteHost };
        _remotePortEntry = new Entry { Keyboard = Keyboard.Numeric, Text = saved.RemotePortText };
        _sharedKeyEntry = new Entry { Keyboard = Keyboard.Text, Text = saved.SharedKey, IsPassword = true };
        _routingRulesContainer = new VerticalStackLayout { Spacing = 8 };
        _addRoutingRuleButton = new Button { Text = "Add Routing Rule" };
        _socks5AuthenticationSwitch = new Switch { IsToggled = saved.EnableSocks5Authentication };
        _socks5UsernameEntry = new Entry { Keyboard = Keyboard.Text, Text = saved.Socks5Username };
        _socks5PasswordEntry = new Entry { Keyboard = Keyboard.Text, Text = saved.Socks5Password, IsPassword = true };
        _compressionSwitch = new Switch { IsToggled = saved.EnableCompression };
        _compressionPicker = BuildCompressionPicker(selectedCompression);
        _cipherPicker = BuildCipherPicker(selectedCipher);
        _socks5UsernameRow = BuildRow("SOCKS5 Username", _socks5UsernameEntry);
        _socks5PasswordRow = BuildRow("SOCKS5 Password", _socks5PasswordEntry);
        _statusLabel = new Label { Text = "Stopped", TextColor = Colors.DarkRed };
        _logEditor = new Editor
        {
            IsReadOnly = true,
            AutoSize = EditorAutoSizeOption.Disabled,
            HeightRequest = 240,
            FontFamily = "monospace"
        };
        _copyLogsButton = new Button
        {
            Text = "Copy Logs",
            HorizontalOptions = LayoutOptions.End
        };
        _startButton = new Button { Text = "Start" };
        _stopButton = new Button { Text = "Stop", IsEnabled = false };

        _startButton.Clicked += StartButtonOnClicked;
        _stopButton.Clicked += StopButtonOnClicked;
        _addRoutingRuleButton.Clicked += (_, _) =>
        {
            AddRoutingRuleRow(new ProxyRoutingRule(string.Empty, TrafficRouteDecision.Tunnel));
            RefreshRoutingRuleRows();
            SaveUiDraft();
        };
        _copyLogsButton.Clicked += async (_, _) => await CopyLogsAsync();
        AppLog.LineAdded += OnLogLineAdded;
        _compressionSwitch.Toggled += (_, e) => _compressionPicker.IsEnabled = e.Value;
        _listenPortEntry.TextChanged += (_, _) => SaveUiDraft();
        _remoteHostEntry.TextChanged += (_, _) => SaveUiDraft();
        _remotePortEntry.TextChanged += (_, _) => SaveUiDraft();
        _sharedKeyEntry.TextChanged += (_, _) => SaveUiDraft();
        _socks5AuthenticationSwitch.Toggled += (_, e) =>
        {
            SetSocks5AuthenticationVisibility(e.Value);
            SaveUiDraft();
        };
        _socks5UsernameEntry.TextChanged += (_, _) => SaveUiDraft();
        _socks5PasswordEntry.TextChanged += (_, _) => SaveUiDraft();
        _compressionSwitch.Toggled += (_, _) => SaveUiDraft();
        _compressionPicker.SelectedIndexChanged += (_, _) => SaveUiDraft();
        _cipherPicker.SelectedIndexChanged += (_, _) => SaveUiDraft();
        _compressionPicker.IsEnabled = _compressionSwitch.IsToggled;
        SetSocks5AuthenticationVisibility(_socks5AuthenticationSwitch.IsToggled);
        LoadRoutingRules(saved.RoutingRules);
        _logEditor.Text = string.Empty;
        _logEditor.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await CopyLogsAsync())
        });

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    BuildRow("Local SOCKS5 Port", _listenPortEntry),
                    BuildRow("Tunnel Host", _remoteHostEntry),
                    BuildRow("Tunnel Port", _remotePortEntry),
                    BuildRow("Shared Key", _sharedKeyEntry),
                    new Label { Text = "Traffic Routing Rules" },
                    new Label { Text = "Rules are checked top to bottom. Use `*`, host patterns, or CIDR prefixes." },
                    _routingRulesContainer,
                    _addRoutingRuleButton,
                    BuildSwitchRow("Enable SOCKS5 Authentication", _socks5AuthenticationSwitch),
                    _socks5UsernameRow,
                    _socks5PasswordRow,
                    BuildSwitchRow("Enable Compression", _compressionSwitch),
                    BuildRow("Compression Algorithm", _compressionPicker),
                    BuildRow("Cipher Algorithm", _cipherPicker),
                    new HorizontalStackLayout
                    {
                        Spacing = 10,
                        Children = { _startButton, _stopButton }
                    },
                    new Label { Text = "Status" },
                    _statusLabel,
                    new HorizontalStackLayout
                    {
                        Children =
                        {
                            new Label
                            {
                                Text = "Logs",
                                VerticalOptions = LayoutOptions.Center
                            },
                            _copyLogsButton
                        }
                    },
                    _logEditor
                }
            }
        };

        RefreshLogView();
    }

    private async void StartButtonOnClicked(object? sender, EventArgs e)
    {
        if (!TryBuildOptions(out var options, out var validationError))
        {
            ShowStatus(validationError, isError: true);
            return;
        }

        try
        {
            SaveUiDraft();
#if ANDROID
            await SocksVpnServiceClient.StartAsync(options, CancellationToken.None);
            ShowStatus("Starting VPN service...", isError: false);
            SetRunningUi(isRunning: true);
#else
            await _proxyRunner.StartAsync(options, CancellationToken.None);
            SetRunningUi(isRunning: true);
#endif
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to start proxy: {ex.Message}", isError: true);
            AppLog.Write($"UI start failed: {ex}");
        }
    }

    private async void StopButtonOnClicked(object? sender, EventArgs e)
    {
        Task stopTask;
#if ANDROID
        SocksVpnServiceClient.Stop();
        stopTask = Task.CompletedTask;
#else
        stopTask = _proxyRunner.StopAsync();
#endif

        await stopTask;
#if !ANDROID
        SetRunningUi(isRunning: false);
#endif
    }

    private void OnStatusChanged(string message)
    {
        Dispatcher.Dispatch(() =>
        {
            var isError = message.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || message.Contains("fail", StringComparison.OrdinalIgnoreCase);
            ShowStatus(message, isError);
            SetRunningUi(_proxyRunner.IsRunning);
        });
    }

    private void OnLogLineAdded(string line)
    {
        Dispatcher.Dispatch(() => AppendLog(line));
    }

    private void AppendLog(string line)
    {
        RefreshLogView();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshLogView();
    }

    private async Task CopyLogsAsync()
    {
        var text = _logEditor.Text ?? string.Empty;
        await Clipboard.Default.SetTextAsync(text);
        ShowStatus("Logs copied to clipboard.", isError: false);
    }

    private void SaveUiDraft()
    {
        var compression = _compressionPicker.SelectedItem is PayloadCompressionAlgorithm c
            ? c
            : PayloadCompressionAlgorithm.Deflate;
        var cipher = _cipherPicker.SelectedItem is TunnelCipherAlgorithm t
            ? t
            : TunnelCipherAlgorithm.ChaCha20Poly1305;

        _configStore.Save(UiConfigStore.UiConfigDraft.FromControls(
            _listenPortEntry.Text ?? string.Empty,
            _remoteHostEntry.Text ?? string.Empty,
            _remotePortEntry.Text ?? string.Empty,
            _sharedKeyEntry.Text ?? string.Empty,
            GetRoutingRulesFromUi(),
            _socks5AuthenticationSwitch.IsToggled,
            _socks5UsernameEntry.Text ?? string.Empty,
            _socks5PasswordEntry.Text ?? string.Empty,
            _compressionSwitch.IsToggled,
            compression,
            cipher));
    }

    private bool TryBuildOptions(out ProxyOptions options, out string validationError)
    {
        options = ProxyOptions.CreateDefaults();
        validationError = string.Empty;

        if (!int.TryParse(_listenPortEntry.Text, out var listenPort) || listenPort is < 1 or > 65535)
        {
            validationError = "Invalid local SOCKS5 port.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_remoteHostEntry.Text))
        {
            validationError = "Tunnel host is required.";
            return false;
        }

        if (!int.TryParse(_remotePortEntry.Text, out var remotePort) || remotePort is < 1 or > 65535)
        {
            validationError = "Invalid tunnel port.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_sharedKeyEntry.Text))
        {
            validationError = "Shared key is required.";
            return false;
        }

        List<ProxyRoutingRule> routingRules;
        try
        {
            routingRules = GetValidatedRoutingRules();
        }
        catch (InvalidDataException ex)
        {
            validationError = ex.Message;
            return false;
        }

        if (_socks5AuthenticationSwitch.IsToggled)
        {
            try
            {
                _ = new Socks5AuthenticationOptions(
                    _socks5UsernameEntry.Text?.Trim() ?? string.Empty,
                    _socks5PasswordEntry.Text ?? string.Empty);
            }
            catch (ArgumentOutOfRangeException ex) when (string.Equals(ex.ParamName, "username", StringComparison.Ordinal))
            {
                validationError = string.IsNullOrWhiteSpace(_socks5UsernameEntry.Text)
                    ? "SOCKS5 username is required."
                    : "SOCKS5 username must be 1..255 bytes.";
                return false;
            }
            catch (ArgumentOutOfRangeException ex) when (string.Equals(ex.ParamName, "password", StringComparison.Ordinal))
            {
                validationError = string.IsNullOrEmpty(_socks5PasswordEntry.Text)
                    ? "SOCKS5 password is required."
                    : "SOCKS5 password must be 1..255 bytes.";
                return false;
            }
            catch (ArgumentException)
            {
                validationError = "SOCKS5 username is required.";
                return false;
            }
        }

        var compressionAlgorithm = _compressionPicker.SelectedItem is PayloadCompressionAlgorithm selectedCompression
            ? selectedCompression
            : PayloadCompressionAlgorithm.Deflate;
        var cipherAlgorithm = _cipherPicker.SelectedItem is TunnelCipherAlgorithm selectedCipher
            ? selectedCipher
            : TunnelCipherAlgorithm.ChaCha20Poly1305;

        options = new ProxyOptions(
            listenPort,
            _remoteHostEntry.Text.Trim(),
            remotePort,
            _sharedKeyEntry.Text,
            routingRules,
            _socks5AuthenticationSwitch.IsToggled,
            _socks5UsernameEntry.Text?.Trim() ?? string.Empty,
            _socks5PasswordEntry.Text ?? string.Empty,
            ProtocolConstants.Version,
            _compressionSwitch.IsToggled,
            compressionAlgorithm,
            cipherAlgorithm);

        return true;
    }

    private void ShowStatus(string message, bool isError)
    {
        _statusLabel.Text = message;
        _statusLabel.TextColor = isError ? Colors.DarkRed : Colors.DarkGreen;
    }

    private void SetRunningUi(bool isRunning)
    {
        _startButton.IsEnabled = !isRunning;
        _stopButton.IsEnabled = isRunning;
    }

    private void SetSocks5AuthenticationVisibility(bool enabled)
    {
        _socks5UsernameRow.IsVisible = enabled;
        _socks5PasswordRow.IsVisible = enabled;
    }

    private void LoadRoutingRules(IReadOnlyList<ProxyRoutingRule> routingRules)
    {
        _routingRuleRows.Clear();
        foreach (var rule in routingRules)
        {
            AddRoutingRuleRow(rule);
        }

        if (_routingRuleRows.Count == 0)
        {
            AddRoutingRuleRow(new ProxyRoutingRule("*", TrafficRouteDecision.Tunnel));
        }

        RefreshRoutingRuleRows();
    }

    private void AddRoutingRuleRow(ProxyRoutingRule rule)
    {
        var row = new RoutingRuleEditorRow(rule);
        row.MatchEntry.TextChanged += (_, _) => SaveUiDraft();
        row.DecisionPicker.SelectedIndexChanged += (_, _) => SaveUiDraft();
        row.MoveUpButton.Clicked += (_, _) =>
        {
            MoveRoutingRule(row, -1);
            SaveUiDraft();
        };
        row.MoveDownButton.Clicked += (_, _) =>
        {
            MoveRoutingRule(row, 1);
            SaveUiDraft();
        };
        row.DeleteButton.Clicked += (_, _) =>
        {
            RemoveRoutingRule(row);
            SaveUiDraft();
        };

        _routingRuleRows.Add(row);
    }

    private void RefreshRoutingRuleRows()
    {
        _routingRulesContainer.Children.Clear();

        for (var index = 0; index < _routingRuleRows.Count; index++)
        {
            var row = _routingRuleRows[index];
            row.PositionLabel.Text = $"Rule #{index + 1}";
            row.MoveUpButton.IsEnabled = index > 0;
            row.MoveDownButton.IsEnabled = index < _routingRuleRows.Count - 1;
            row.DeleteButton.IsEnabled = _routingRuleRows.Count > 1;
            _routingRulesContainer.Children.Add(row.Root);
        }
    }

    private void MoveRoutingRule(RoutingRuleEditorRow row, int offset)
    {
        var index = _routingRuleRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var newIndex = index + offset;
        if (newIndex < 0 || newIndex >= _routingRuleRows.Count)
        {
            return;
        }

        _routingRuleRows.RemoveAt(index);
        _routingRuleRows.Insert(newIndex, row);
        RefreshRoutingRuleRows();
    }

    private void RemoveRoutingRule(RoutingRuleEditorRow row)
    {
        _routingRuleRows.Remove(row);
        if (_routingRuleRows.Count == 0)
        {
            AddRoutingRuleRow(new ProxyRoutingRule("*", TrafficRouteDecision.Tunnel));
        }

        RefreshRoutingRuleRows();
    }

    private List<ProxyRoutingRule> GetRoutingRulesFromUi()
    {
        return _routingRuleRows
            .Select(row => new ProxyRoutingRule(
                row.MatchEntry.Text ?? string.Empty,
                row.GetDecision()))
            .ToList();
    }

    private List<ProxyRoutingRule> GetValidatedRoutingRules()
    {
        var rules = GetRoutingRulesFromUi();
        if (rules.Count == 0)
        {
            return [new ProxyRoutingRule("*", TrafficRouteDecision.Tunnel)];
        }

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            try
            {
                _ = TrafficRoutingRuleFactory.Create(rule.Match, rule.Decision);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"Routing rule #{index + 1} is invalid: {ex.Message}");
            }
        }

        return rules;
    }

    private void RefreshLogView()
    {
        var text = AppLog.GetText();
        _logEditor.Text = text;
        _logEditor.CursorPosition = text.Length;
    }

    private static View BuildRow(string caption, View control)
    {
        return new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = caption },
                control
            }
        };
    }

    private static View BuildSwitchRow(string caption, Switch control)
    {
        return new HorizontalStackLayout
        {
            Spacing = 12,
            Children =
            {
                new Label { Text = caption, VerticalOptions = LayoutOptions.Center },
                control
            }
        };
    }

    private static Picker BuildCompressionPicker(PayloadCompressionAlgorithm selected)
    {
        var values = Enum.GetValues<PayloadCompressionAlgorithm>().ToList();
        var picker = new Picker { ItemsSource = values };
        picker.SelectedItem = selected;
        return picker;
    }

    private static Picker BuildCipherPicker(TunnelCipherAlgorithm selected)
    {
        var values = Enum.GetValues<TunnelCipherAlgorithm>().ToList();
        var picker = new Picker { ItemsSource = values };
        picker.SelectedItem = selected;
        return picker;
    }

    private sealed class RoutingRuleEditorRow
    {
        public RoutingRuleEditorRow(ProxyRoutingRule rule)
        {
            PositionLabel = new Label();
            MatchEntry = new Entry
            {
                Keyboard = Keyboard.Text,
                Text = rule.Match,
                Placeholder = "*, *.example.com, 10.0.0.0/8"
            };

            var decisions = Enum.GetValues<TrafficRouteDecision>().ToList();
            DecisionPicker = new Picker { ItemsSource = decisions, WidthRequest = 120 };
            DecisionPicker.SelectedItem = decisions.Contains(rule.Decision)
                ? rule.Decision
                : TrafficRouteDecision.Tunnel;

            MoveUpButton = new Button { Text = "Up", WidthRequest = 72 };
            MoveDownButton = new Button { Text = "Down", WidthRequest = 72 };
            DeleteButton = new Button { Text = "Delete", WidthRequest = 84 };

            Root = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    PositionLabel,
                    MatchEntry,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children = { DecisionPicker, MoveUpButton, MoveDownButton, DeleteButton }
                    }
                }
            };
        }

        public View Root { get; }
        public Label PositionLabel { get; }
        public Entry MatchEntry { get; }
        public Picker DecisionPicker { get; }
        public Button MoveUpButton { get; }
        public Button MoveDownButton { get; }
        public Button DeleteButton { get; }

        public TrafficRouteDecision GetDecision()
        {
            return DecisionPicker.SelectedItem is TrafficRouteDecision decision
                ? decision
                : TrafficRouteDecision.Tunnel;
        }
    }
}
