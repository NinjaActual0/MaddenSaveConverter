using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MaddenSaveConverter.Convert;
using MaddenSaveConverter.Tdb2;

namespace MaddenSaveConverter;

public partial class MainWindow : Window
{
    // default save locations + bundled templates (shared with CLI)
    static readonly string M26Saves = Cli.M26Saves;
    static readonly string M27Saves = Cli.M27Saves;
    static string TplM26 => Cli.TplM26;
    static string TplM27 => Cli.TplM27;

    string _sourcePath;

    public MainWindow()
    {
        InitializeComponent();
        Log("LEGION OF LOOM MADDEN SAVE CONVERTER");
        Log("------------------------------------");
        Log($"M26 saves: {M26Saves}");
        Log($"M27 saves: {M27Saves}");
        Log("");
    }

    void Log(string s)
    {
        LogBox.AppendText(s + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick a save file",
            InitialDirectory = Directory.Exists(M27Saves) ? M27Saves : M26Saves,
            Filter = "Save files (ROSTER-*, CAREERDRAFT-*)|ROSTER-*;CAREERDRAFT-*|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            SourcePathBox.Text = _sourcePath = dlg.FileName;
    }

    void SourcePath_TextChanged(object sender, TextChangedEventArgs e)
    {
        DetectPanel.Visibility = Visibility.Collapsed;
        ConvertBtn.IsEnabled = false;
        OutputNote.Text = "";
        if (!File.Exists(SourcePathBox.Text)) return;

        try
        {
            var header = new byte[0x80];
            using (var fs = File.OpenRead(SourcePathBox.Text))
                if (fs.Read(header, 0, header.Length) < 0x2E) return;

            if (System.Text.Encoding.ASCII.GetString(header, 0, 8) != "FBCHUNKS")
            {
                DetectPanel.Visibility = Visibility.Visible;
                DetectText.Text = $"{Path.GetFileName(SourcePathBox.Text)} — not an FBCHUNKS save (draft classes and franchise files come in a later build)";
                return;
            }

            var all = System.Text.Encoding.ASCII.GetString(header, 0, header.Length);
            string ver;
            int end = Array.IndexOf(header, (byte)0, 0x2E);
            ver = System.Text.Encoding.ASCII.GetString(header, 0x2E, end < 0 ? 40 : end - 0x2E);
            if (all.Contains("Madden-27")) ver = "Madden-27";
            else if (all.Contains("Madden-26")) ver = "Madden-26";
            string from, to, tpl;
            bool isDraft = Path.GetFileName(SourcePathBox.Text).StartsWith("CAREERDRAFT", StringComparison.OrdinalIgnoreCase);
            if (ver.Contains("Madden-27"))
            {
                from = "Madden 27"; to = "Madden 26"; tpl = isDraft ? Cli.TplDraftM26 : TplM26;
                OutputNote.Text = $"Output → {(isDraft ? M26Saves : M26Saves)}\\{Path.GetFileName(SourcePathBox.Text)}";
            }
            else if (ver.Contains("Madden-26"))
            {
                from = "Madden 26"; to = "Madden 27"; tpl = isDraft ? Cli.TplDraftM27 : TplM27;
                OutputNote.Text = $"Output → {(isDraft ? M27Saves : M27Saves)}\\{Path.GetFileName(SourcePathBox.Text)}";
            }
            else
            {
                DetectPanel.Visibility = Visibility.Visible;
                DetectIcon.Text = "!";
                DetectText.Text = $"Unrecognized version: {ver} — only Madden 26/27 rosters are supported right now";
                return;
            }

            DetectPanel.Visibility = Visibility.Visible;
            DetectText.Text = $"Detected: {from} roster ({ver})   →   will convert for {to}";

            ConvertBtn.IsEnabled = true;
            ConvertBtn.Tag = tpl;
        }
        catch (Exception ex)
        {
            Log("[error] " + ex.Message);
        }
    }

    async void Convert_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConvertBtn.IsEnabled = false;
            var srcPath = SourcePathBox.Text;
            var tplPath = (string)ConvertBtn.Tag;
            bool toM27 = tplPath == TplM27;
            bool isDraft = Path.GetFileName(srcPath).StartsWith("CAREERDRAFT", StringComparison.OrdinalIgnoreCase);
            string outDir = toM27 ? M27Saves : M26Saves;
            string outPath = Path.Combine(outDir, Path.GetFileName(srcPath));

            Log("");
            Log($"converting: {srcPath}");
            Log($"type:       {(isDraft ? "draft class" : "roster")}");
            Log($"direction:  {(toM27 ? "M26 → M27" : "M27 → M26")}");

            var res = await System.Threading.Tasks.Task.Run(() =>
            {
                if (isDraft)
                    return toM27 ? DraftConverter.M26ToM27(srcPath, tplPath, outPath)
                                 : DraftConverter.M27ToM26(srcPath, tplPath, outPath);
                var src = RosterFile.Load(srcPath);
                var tpl = RosterFile.Load(tplPath);
                return toM27
                    ? RosterProfiles.M26ToM27(src, tpl, outPath)
                    : RosterProfiles.M27ToM26(src, tpl, outPath);
            });

            foreach (var l in res.Log) Log("  " + l);

            Log("verify:");
            bool ok = isDraft ? File.Exists(outPath) : RosterFile.Verify(outPath, m => Log("  " + m));
            if (ok)
            {
                Log($"DONE → {outPath}");
                Log("back up your saves before loading this in-game!");
            }
            else
            {
                Log("FAILED verification - output was NOT written correctly");
            }
            ConvertBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log("[error] " + ex.Message);
            ConvertBtn.IsEnabled = true;
        }
    }
}
