using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClipStitch;

internal static class DialogTheme
{
    public static void Apply(Window dialog, Window owner, string title)
    {
        dialog.Owner = owner;
        dialog.Title = title;
        foreach (var key in owner.Resources.Keys) dialog.Resources[key] = owner.Resources[key];
        dialog.Background = (Brush)new BrushConverter().ConvertFrom("#0C1729")!;
        dialog.Foreground = (Brush)new BrushConverter().ConvertFrom("#E7F1FA")!;
        dialog.FontFamily = new FontFamily("Segoe UI");
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.ShowInTaskbar = false;
        dialog.SizeToContent = SizeToContent.WidthAndHeight;
    }
}

/// <summary>Asks for the output file name; the name without its extension is preselected for quick editing.</summary>
internal sealed class SaveNameDialog : Window
{
    private readonly TextBox nameBox = new() { Width = 420 };
    private readonly TextBlock folderText = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330, Margin = new Thickness(3, 0, 6, 0) };
    private readonly TextBlock errorText = new() { Foreground = Brushes.Salmon, Margin = new Thickness(3, 2, 3, 0), Visibility = Visibility.Collapsed };
    private string folder;

    public string FilePath => Path.Combine(folder, NormalizedName());

    public SaveNameDialog(Window owner, string folder, string defaultName)
    {
        DialogTheme.Apply(this, owner, "Save joined video");
        this.folder = folder;
        folderText.Text = folder;
        folderText.ToolTip = folder;
        nameBox.Text = defaultName;

        var change = new Button { Content = "Change folder…", ToolTip = "Choose the destination folder" };
        change.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "Choose where to save the video", InitialDirectory = this.folder };
            if (picker.ShowDialog(this) != true) return;
            this.folder = picker.FolderName;
            folderText.Text = this.folder;
            folderText.ToolTip = this.folder;
            nameBox.Focus();
        };
        var ok = new Button { Content = "Save", IsDefault = true, MinWidth = 90, ToolTip = "Save the joined video with this name" };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without saving" };

        var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        folderRow.Children.Add(new TextBlock { Text = "Folder:", Foreground = Brushes.LightSteelBlue });
        DockPanel.SetDock(change, Dock.Right);
        folderRow.Children.Add(change);
        folderRow.Children.Add(folderText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(folderRow);
        root.Children.Add(new TextBlock { Text = "File name", Margin = new Thickness(3, 0, 0, 0) });
        root.Children.Add(nameBox);
        root.Children.Add(errorText);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            nameBox.Focus();
            nameBox.Select(0, Path.GetFileNameWithoutExtension(nameBox.Text).Length);
        };
    }

    private string NormalizedName()
    {
        string name = nameBox.Text.Trim();
        return name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? name : name + ".mp4";
    }

    private void Accept()
    {
        string name = nameBox.Text.Trim();
        string? error = name.Length == 0 || Path.GetFileNameWithoutExtension(NormalizedName()).Trim().Length == 0 ? "Enter a file name."
            : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "The name contains characters that are not allowed in file names."
            : !Directory.Exists(folder) ? "The folder does not exist."
            : null;
        if (error is not null)
        {
            errorText.Text = error;
            errorText.Visibility = Visibility.Visible;
            nameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}

/// <summary>Modal progress window that keeps the app unclickable until the export finishes, fails, or is cancelled.</summary>
internal sealed class ExportProgressDialog : Window
{
    private readonly ProgressBar bar = new() { Width = 380, Height = 18, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock percent = new() { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button cancel = new() { Content = "Cancel export", ToolTip = "Cancel saving and leave the destination unchanged",
        HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
    private bool finished;

    public event Action? CancelRequested;

    public ExportProgressDialog(Window owner, string message)
    {
        DialogTheme.Apply(this, owner, "Saving video");
        WindowStyle = WindowStyle.ToolWindow;
        bar.Foreground = (Brush)new BrushConverter().ConvertFrom("#3FC48A")!;
        bar.Background = (Brush)new BrushConverter().ConvertFrom("#14263C")!;
        cancel.Click += (_, _) => RequestCancel();
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        root.Children.Add(bar);
        root.Children.Add(percent);
        root.Children.Add(cancel);
        Content = root;
        Report(0);
        Closing += (_, e) =>
        {
            if (finished) return;
            e.Cancel = true;
            RequestCancel();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) RequestCancel(); };
    }

    public void Report(double value)
    {
        bar.Value = value;
        percent.Text = $"{value * 100:0}%";
    }

    public void Finish()
    {
        finished = true;
        Close();
    }

    private void RequestCancel()
    {
        cancel.IsEnabled = false;
        cancel.Content = "Cancelling…";
        CancelRequested?.Invoke();
    }
}
