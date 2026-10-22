using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>
/// A key's state — never its value — and a Change… button. The value goes straight to
/// <see cref="ISecretStore"/> (Credential Manager); settings only hold the key's name.
/// </summary>
sealed class KeyField : TableLayoutPanel
{
    readonly PageHost _host;
    readonly Func<AppSettings, string?> _name;
    readonly Action<AppSettings, string> _setName;
    readonly string _defaultName;
    readonly Func<string> _url;
    readonly Func<string?>? _instead;
    readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 3, 3, 3) };

    /// <param name="what">"summary", "transcription"… as used in "Paste your summary key".</param>
    /// <param name="bearer">An OpenAI-style key, which may be shared with another server on the same host.</param>
    /// <param name="instead">When no key is saved: why that is fine (e.g. another key is used), or null.</param>
    public KeyField(PageHost host, string what, bool bearer, Func<AppSettings, string?> name, Action<AppSettings, string> setName,
        string defaultName, Func<string> url, Func<string?>? instead = null)
    {
        _host = host;
        What = what;
        Bearer = bearer;
        _name = name;
        _setName = setName;
        _defaultName = defaultName;
        _url = url;
        _instead = instead;

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left;
        Margin = new Padding(0);
        ColumnCount = 2;
        RowCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(Ui.Button("Change…", Change), 0, 0);
        Controls.Add(_status, 1, 0);
    }

    public string What { get; }
    public bool Bearer { get; }

    /// <summary>Raised after a new key was saved, with its value, so the page can offer it to servers on the same host.</summary>
    public event Action<KeyField, string>? KeySaved;

    string? KeyName() => Ui.Blank(_name(_host.Edited));

    public bool IsSaved => KeyName() is { } n && _host.Secrets.Get(n) is { Length: > 0 };

    public string ServerUrl() => _url().Trim();

    public void UpdateStatus()
    {
        if (KeyName() is null)
        {
            _status.Text = "none (this server doesn't need one)";
            _status.ForeColor = SystemColors.GrayText;
        }
        else if (IsSaved)
        {
            _status.Text = "•••••••• saved";
            _status.ForeColor = SystemColors.ControlText;
        }
        else
        {
            var instead = _instead?.Invoke();
            _status.Text = instead ?? "not saved";
            _status.ForeColor = instead is null ? Ui.Bad : SystemColors.GrayText;
        }
    }

    /// <summary>Saves <paramref name="value"/> under this field's key name (giving it the default name if it had none).</summary>
    public void Store(string value)
    {
        var name = KeyName();
        if (name is null)
        {
            name = _defaultName;
            _setName(_host.Edited, name);
        }
        // Pasted from a web page or a PowerShell pipe, a key can carry invisible marks; HTTP headers allow only ASCII.
        var clean = new string([.. value.Where(c => c is not ('﻿' or '​' or '‌' or '‍' or ' '))]).Trim();
        if (clean.Any(c => c < 0x21 || c > 0x7E))
            throw new InvalidOperationException("That key has characters a web request can't carry (spaces or non-ASCII letters). Copy it again from where it was issued.");
        _host.Secrets.Set(name, clean);
        UpdateStatus();
    }

    void Change()
    {
        bool saved = IsSaved;
        using var dialog = new KeyDialog(What, saved);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            if (dialog.Remove)
            {
                if (KeyName() is { } name) _host.Secrets.Delete(name);
            }
            else
            {
                var value = dialog.Value;
                Store(value);
                KeySaved?.Invoke(this, value);
            }
        }
        catch (InvalidOperationException ex)
        {
            // CredWrite's message names the key and a Win32 error code, never the value.
            Ui.Error(this, $"The key couldn't be saved: {ex.Message}");
        }
        UpdateStatus();
    }
}
