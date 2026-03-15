using SimpleShadowsocks.Client.Maui.Services;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;
using System.Text;

namespace SimpleShadowsocks.Client.Maui;

public sealed class MainPage : ContentPage
{
    private readonly ProxyRunner _proxyRunner;
    private readonly UiConfigStore _configStore;
    private readonly Entry _listenPortEntry;
    private readonly Entry _remoteHostEntry;
    private readonly Entry _remotePortEntry;
    private readonly Entry _sharedKeyEntry;
    private readonly Switch _compressionSwitch;
    private readonly Picker _compressionPicker;
    private readonly Picker _cipherPicker;
    private readonly Label _statusLabel;
    private readonly Editor _logEditor;
    private readonly Button _copyLogsButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly StringBuilder _logBuffer = new();

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
        _compressionSwitch = new Switch { IsToggled = saved.EnableCompression };
        _compressionPicker = BuildCompressionPicker(selectedCompression);
        _cipherPicker = BuildCipherPicker(selectedCipher);
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
        _copyLogsButton.Clicked += async (_, _) => await CopyLogsAsync();
        AppLog.LineAdded += OnLogLineAdded;
        _compressionSwitch.Toggled += (_, e) => _compressionPicker.IsEnabled = e.Value;
        _listenPortEntry.TextChanged += (_, _) => SaveUiDraft();
        _remoteHostEntry.TextChanged += (_, _) => SaveUiDraft();
        _remotePortEntry.TextChanged += (_, _) => SaveUiDraft();
        _sharedKeyEntry.TextChanged += (_, _) => SaveUiDraft();
        _compressionSwitch.Toggled += (_, _) => SaveUiDraft();
        _compressionPicker.SelectedIndexChanged += (_, _) => SaveUiDraft();
        _cipherPicker.SelectedIndexChanged += (_, _) => SaveUiDraft();
        _compressionPicker.IsEnabled = _compressionSwitch.IsToggled;
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
        _logBuffer.AppendLine(line);
        _logEditor.Text = _logBuffer.ToString();
        _logEditor.CursorPosition = _logEditor.Text.Length;
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
}
