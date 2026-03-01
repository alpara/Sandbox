using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace X2T.BarcodeReader.Module.Hardware.DialogReader;

public sealed class DialogReaderV0 : BarcodeReaderBase, IBarcodeReader, IDisposable, IAsyncDisposable
{
    private DialogReaderConfigV0 Cfg => (DialogReaderConfigV0)_cfg;

    private readonly SemaphoreSlim _promptLock = new(1, 1);

    public bool IsConnected => true;

    public event EventHandler<BarcodeReadEventArgs> BarcodeReceived = delegate { };

    public DialogReaderV0(DialogReaderConfigV0 cfg, ILogger logger) : base(cfg, logger) { }

    public void Connect()
    {
        _logger.LogInformation("DialogReader '{Name}': Connect", Name);
    }

    public void Disconnect()
    {
        _logger.LogInformation("DialogReader '{Name}': Disconnect", Name);
    }

    public void Start()
    {
        _logger.LogWarning("DialogReader '{Name}': Start ignored (DialogReader does not support continuous mode).", Name);
    }

    public void Stop()
    {
        _logger.LogWarning("DialogReader '{Name}': Stop ignored (DialogReader does not support continuous mode).", Name);
    }

    /// <summary>
    /// Synchronous trigger (opens a modal WPF dialog). Cancel => no barcode emitted.
    /// Safe for UI-thread calls (no Dispatcher.Invoke deadlock).
    /// </summary>
    public void Trigger()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("DialogReader '{Name}': No WPF Application dispatcher available. Trigger ignored.", Name);
            return;
        }

        // If we are already on the UI thread, show the dialog directly.
        // Otherwise marshal to UI thread and block until it closes.
        var text = dispatcher.CheckAccess()
            ? PromptBarcodeTextOnUiThread()
            : dispatcher.Invoke(PromptBarcodeTextOnUiThread);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogInformation("DialogReader '{Name}': Dialog cancelled or empty input -> no barcode.", Name);
            return;
        }

        EmitBarcode(text);
    }

    /// <summary>
    /// Async trigger (opens a modal WPF dialog on UI thread). Cancel => no barcode emitted.
    /// </summary>
    public async Task TriggerAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("DialogReader '{Name}': No WPF Application dispatcher available. Trigger ignored.", Name);
            return;
        }

        // Ensure only one dialog is opened at a time per reader instance.
        await _promptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? text;

            if (dispatcher.CheckAccess())
            {
                text = PromptBarcodeTextOnUiThread();
            }
            else
            {
                // InvokeAsync avoids some classic deadlock patterns (vs Invoke), and keeps things consistent.
                text = await dispatcher.InvokeAsync(
                        PromptBarcodeTextOnUiThread,
                        DispatcherPriority.Normal,
                        ct)
                    .Task.ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("DialogReader '{Name}': Dialog cancelled or empty input -> no barcode.", Name);
                return;
            }

            EmitBarcode(text);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    private string? PromptBarcodeTextOnUiThread()
    {
        // Must run on UI thread!
        var owner = TryGetActiveWindow();

        var initialText = GenerateInitialText();
        var dlg = new BarcodeInputDialog(
            title: $"Barcode Input - {Name}",
            initialText: initialText)
        {
            Owner = owner,
            WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = true // helpful in diagnostic tooling; remove if you dislike it
        };

        var ok = dlg.ShowDialog() == true;
        if (!ok)
            return null;

        var text = dlg.BarcodeText?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Window? TryGetActiveWindow()
    {
        var app = Application.Current;
        if (app is null)
            return null;

        foreach (Window w in app.Windows)
        {
            if (w.IsActive)
                return w;
        }

        return app.MainWindow;
    }

    private string GenerateInitialText()
    {
        // You can use the config as "suggested text".
        // If you want a blank dialog by default, just: return string.Empty;
        var fmt = NormalizePattern(Cfg.InitialPattern);
        var now = DateTime.Now;
        return now.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static string NormalizePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "yyyyMMddHHmmss-fff";

        // Accept common "Java-ish"/user patterns and convert to .NET date format where sensible.
        // Note: .NET uses MM=month, mm=minute.
        return pattern
            .Replace("YYYY", "yyyy", StringComparison.Ordinal)
            .Replace("DD", "dd", StringComparison.Ordinal)
            .Replace("hh", "HH", StringComparison.Ordinal);
    }

    private void EmitBarcode(string text)
    {
        _logger.LogInformation("DialogReader '{ReaderName}': Barcode = {Text}", Name, text);

        try
        {
            BarcodeReceived(this, new BarcodeReadEventArgs(Id, text));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DialogReader '{Name}': BarcodeReceived handler threw.", Name);
        }
    }

    public override void SendCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd))
            return;

        // Accept both "START" and router style tokens like "CMD#START".
        var normalized = cmd.Trim();
        var verb = normalized;

        if (normalized.Contains('#'))
        {
            var parts = normalized.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
                verb = parts[1];
        }

        switch (verb.ToUpperInvariant())
        {
            case "CONNECT":
                Connect();
                break;
            case "DISCONNECT":
                Disconnect();
                break;
            case "START":
                Start();
                break;
            case "STOP":
                Stop();
                break;
            case "TRIGGER":
                Trigger();
                break;
            case "QUIT":
                Quit();
                break;
            default:
                _logger.LogWarning("DialogReader '{Name}': Unknown command '{Cmd}'.", Name, cmd);
                break;
        }
    }

    public void Dispose()
    {
        _promptLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class BarcodeInputDialog : Window
    {
        private readonly TextBox _textBox;

        public string? BarcodeText => _textBox.Text;

        public BarcodeInputDialog(string title, string initialText)
        {
            Title = title;
            Width = 520;
            Height = 170;
            ResizeMode = ResizeMode.NoResize;

            var root = new Grid
            {
                Margin = new Thickness(14)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Please enter barcode text:",
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(label, 0);
            root.Children.Add(label);

            _textBox = new TextBox
            {
                Text = initialText ?? string.Empty,
                Margin = new Thickness(0, 0, 0, 12),
                MinWidth = 460
            };
            Grid.SetRow(_textBox, 1);
            root.Children.Add(_textBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var ok = new Button
            {
                Content = "OK",
                Width = 90,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            ok.Click += (_, __) =>
            {
                DialogResult = true;
                Close();
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                IsCancel = true
            };
            cancel.Click += (_, __) =>
            {
                DialogResult = false;
                Close();
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;

            Loaded += (_, __) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    Close();
                    e.Handled = true;
                }
            };
        }
    }
}
