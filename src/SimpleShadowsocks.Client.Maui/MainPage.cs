using SimpleShadowsocks.Client.Maui.Services;
using SimpleShadowsocks.Protocol;
using SimpleShadowsocks.Protocol.Crypto;

namespace SimpleShadowsocks.Client.Maui;

public sealed class MainPage : ContentPage
{
    private readonly ProxyRunner _proxyRunner;
    private readonly Entry _listenPortEntry;
    private readonly Entry _remoteHostEntry;
    private readonly Entry _remotePortEntry;
    private readonly Entry _sharedKeyEntry;
    private readonly Switch _compressionSwitch;
    private readonly Picker _compressionPicker;
    private readonly Picker _cipherPicker;
    private readonly Label _statusLabel;
    private readonly Button _startButton;
    private readonly Button _stopButton;

    public MainPage(ProxyRunner proxyRunner)
    {
        _proxyRunner = proxyRunner;
        _proxyRunner.StatusChanged += OnStatusChanged;

        Title = "SimpleShadowsocks";
        var defaults = ProxyOptions.CreateDefaults();

        _listenPortEntry = new Entry { Keyboard = Keyboard.Numeric, Text = defaults.ListenPort.ToString() };
        _remoteHostEntry = new Entry { Keyboard = Keyboard.Text, Text = defaults.RemoteHost };
        _remotePortEntry = new Entry { Keyboard = Keyboard.Numeric, Text = defaults.RemotePort.ToString() };
        _sharedKeyEntry = new Entry { Keyboard = Keyboard.Text, Text = defaults.SharedKey, IsPassword = true };
        _compressionSwitch = new Switch { IsToggled = defaults.EnableCompression };
        _compressionPicker = BuildCompressionPicker(defaults.CompressionAlgorithm);
        _cipherPicker = BuildCipherPicker(defaults.TunnelCipherAlgorithm);
        _statusLabel = new Label { Text = "Stopped", TextColor = Colors.DarkRed };
        _startButton = new Button { Text = "Start" };
        _stopButton = new Button { Text = "Stop", IsEnabled = false };

        _startButton.Clicked += StartButtonOnClicked;
        _stopButton.Clicked += StopButtonOnClicked;
        _compressionSwitch.Toggled += (_, e) => _compressionPicker.IsEnabled = e.Value;
        _compressionPicker.IsEnabled = _compressionSwitch.IsToggled;

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
                    _statusLabel
                }
            }
        };
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _proxyRunner.StopAsync();
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
            await _proxyRunner.StartAsync(options, CancellationToken.None);
            SetRunningUi(isRunning: true);
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to start proxy: {ex.Message}", isError: true);
        }
    }

    private async void StopButtonOnClicked(object? sender, EventArgs e)
    {
        await _proxyRunner.StopAsync();
        SetRunningUi(isRunning: false);
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
