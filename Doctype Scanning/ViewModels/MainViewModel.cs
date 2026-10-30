using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Doctype_Scanning.Models;
using Doctype_Scanning.Services;
using Doctype_Scanning.Services.Qc;
using HandyControl.Data;
using HandyControl.Themes;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Doctype_Scanning.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private CiData? _ci;
    private DictionaryLibrary? _dictionaries;
    private List<string> _db3Files = new();
    private List<string> _excelFiles = new();
    private string _db3Folder = string.Empty;
    private CancellationTokenSource? _cts;

    public string Title => "DocType & Typo Checker";

    public ObservableCollection<CiField> Fields { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private CiField? _selectedField;

    [ObservableProperty]
    private string _ciPath = "No CI imported";

    [ObservableProperty]
    private string _ciSummary = string.Empty;

    [ObservableProperty]
    private string _dictionaryPath = "No dictionary imported (optional)";

    [ObservableProperty]
    private string _dictionarySummary = string.Empty;

    /// <summary>Says what the typo check of the selected field is compared with: its dictionary or the most common value.</summary>
    [ObservableProperty]
    private string _referenceHint = string.Empty;

    /// <summary>Pre-delivery QC check 12: a batch with a higher share of Capture images is flagged.</summary>
    [ObservableProperty]
    private double _captureLimitPercent = 20;

    [ObservableProperty]
    private string _sourcePath = "No db3 folder or excel selected";

    [ObservableProperty]
    private string _sourceSummary = string.Empty;

    [ObservableProperty]
    private string _sourceDetails = "No db3 folder or excel selected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StartQcCommand), nameof(ImportCiCommand), nameof(SelectDb3FolderCommand), nameof(SelectExcelFilesCommand), nameof(ClearSourcesCommand), nameof(ImportDictionaryCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _statusText = "Import the CI, select the db3 folder and pick the field to check.";

    [ObservableProperty]
    private bool _isDarkMode = true;

    public bool IsIdle => !IsBusy;

    partial void OnIsDarkModeChanged(bool value) =>
        ThemeManager.Current.ApplicationTheme = value ? ApplicationTheme.Dark : ApplicationTheme.Light;

    partial void OnSelectedFieldChanged(CiField? value) => UpdateReferenceHint();

    /// <summary>
    /// Imports a dictionary file (e.g. Dictionary_24015.txt). It is optional: a field whose CI "Keying Dictionary" is in the
    /// file is checked against that dictionary, every other field against the most common value in the files.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportDictionaryAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select the dictionary file",
            Filter = "Dictionary (*.txt;*.json)|*.txt;*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Progress = 0;
        StatusText = "Reading dictionary...";

        try
        {
            DictionaryLibrary library = await Task.Run(() => DictionaryReader.Read(dialog.FileName));

            _dictionaries = library;
            DictionaryPath = dialog.FileName;
            DictionarySummary = $"{library.Count} dictionaries, {library.ValueCount:N0} values";
            StatusText = "Dictionary imported.";
            UpdateReferenceHint();
        }
        catch (Exception ex)
        {
            StatusText = "Dictionary import failed.";
            MessageBox.Show(ex.Message, "Import dictionary", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateReferenceHint()
    {
        CiField? field = SelectedField;

        if (field == null)
        {
            ReferenceHint = string.Empty;
            return;
        }

        DictionaryLookup? lookup = _dictionaries?.Lookup(field.Dictionaries);

        ReferenceHint = lookup != null
            ? $"Typos are checked against the dictionary {lookup.Names} ({lookup.ValueCount:N0} values)."
            : field.Dictionaries.Count == 0
                ? "No dictionary for this field in the CI: typos are checked against the most common value in the files."
                : _dictionaries == null
                    ? $"The CI names {string.Join(", ", field.Dictionaries)} for this field. Import the dictionary to check against it; until then the most common value in the files is used."
                    : $"{string.Join(", ", field.Dictionaries)} is not in the imported dictionary: the most common value in the files is used.";
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportCiAsync()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select CI excel",
            Filter = "CI Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm"
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        Progress = 0;
        StatusText = "Reading CI...";

        try
        {
            CiData ci = await Task.Run(() => CiReader.Read(dialog.FileName));

            string? previous = SelectedField?.Name;

            _ci = ci;
            CiPath = dialog.FileName;
            CiSummary = $"{ci.Doctypes.Count} doctype(s), {ci.Fields.Count} field(s)";

            Fields.Clear();
            foreach (CiField field in ci.Fields) Fields.Add(field);

            // keep the user's field when a newer CI of the same project is imported
            SelectedField = Fields.FirstOrDefault(f => f.Name.Equals(previous, StringComparison.OrdinalIgnoreCase));

            StartQcCommand.NotifyCanExecuteChanged();
            StatusText = "CI imported. Pick the field to check.";
        }
        catch (Exception ex)
        {
            StatusText = "CI import failed.";
            MessageBox.Show(ex.Message, "Import CI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectDb3Folder()
    {
        OpenFolderDialog dialog = new() { Title = "Select the folder with the db3 files" };

        if (dialog.ShowDialog() != true) return;

        _db3Folder = dialog.FolderName;
        _db3Files = Directory.EnumerateFiles(_db3Folder, "*.db3", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdateSources();
        StatusText = _db3Files.Count == 0 ? "No .db3 file was found in that folder." : "db3 folder selected.";
    }

    /// <summary>
    /// Excel exports of db3s are checked with the same rules as the db3s themselves.
    /// Several files can be picked at once (Ctrl / Shift click), and every press adds to the list, also from other folders.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectExcelFiles()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Select the excel file(s) - Ctrl / Shift click for several",
            Filter = "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        // temp files of an open excel ("~$...") are never real workbooks; a file picked twice is kept once
        int before = _excelFiles.Count;
        _excelFiles = _excelFiles
            .Concat(dialog.FileNames.Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdateSources();
        StatusText = _excelFiles.Count == 0
            ? "No excel file selected."
            : $"{_excelFiles.Count - before} excel file(s) added, {_excelFiles.Count} selected in total.";
    }

    /// <summary>Forgets the selected db3 folder and excel files so a new batch can be chosen.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ClearSources()
    {
        _db3Folder = string.Empty;
        _db3Files = new List<string>();
        _excelFiles = new List<string>();

        UpdateSources();
        StatusText = "Selection cleared.";
    }

    private void UpdateSources()
    {
        List<string> parts = new();
        if (_db3Folder.Length > 0) parts.Add($"Folder: {_db3Folder}");
        if (_excelFiles.Count > 0) parts.Add($"Excel: {string.Join(", ", _excelFiles.Select(Path.GetFileName))}");

        SourcePath = parts.Count == 0 ? "No db3 folder or excel selected" : string.Join("   |   ", parts);

        // the hover text lists every excel on its own line
        List<string> details = new();
        if (_db3Folder.Length > 0) details.Add($"Folder: {_db3Folder}");
        details.AddRange(_excelFiles);
        SourceDetails = details.Count == 0 ? SourcePath : string.Join("\n", details);

        List<string> counts = new();
        if (_db3Folder.Length > 0) counts.Add(_db3Files.Count == 0 ? "no db3 found" : $"{_db3Files.Count} db3");
        if (_excelFiles.Count > 0) counts.Add($"{_excelFiles.Count} excel");
        SourceSummary = string.Join(", ", counts);

        StartCommand.NotifyCanExecuteChanged();
        StartQcCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsBusy && _ci != null && SelectedField != null && (_db3Files.Count + _excelFiles.Count) > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        CiData ci = _ci!;
        CiField field = SelectedField!;
        DictionaryLibrary? dictionaries = _dictionaries;
        List<string> files = _db3Files.Concat(_excelFiles).ToList();

        // the result goes next to the db3s; with excel files only, next to the first one
        string root = _db3Folder.Length > 0 ? _db3Folder : Path.GetDirectoryName(_excelFiles[0])!;

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        Progress = 0;
        StatusText = "Starting...";

        Progress<(int Percent, string Text)> progress = new(p =>
        {
            Progress = p.Percent;
            StatusText = p.Text;
        });

        try
        {
            SourceChecker checker = new(ci, field, dictionaries);
            List<ErrorRecord> errors = await Task.Run(() => checker.Run(files, root, progress, ct), ct);
            IReadOnlyList<DictionaryNote> notes = checker.Notes;

            Progress = 95;
            StatusText = "Writing excel...";

            string outputPath = Path.Combine(root, $"DocType_Typo_Check_{field.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            await Task.Run(() => ExcelExporter.Export(outputPath, errors, notes), ct);

            Progress = 100;
            StatusText = $"Done - {errors.Count:N0} error(s) in {files.Count} file(s). Saved to {outputPath}";

            string question = errors.Count == 0
                ? $"No errors found in {files.Count} file(s).\n\nThe (empty) result was saved to:\n{outputPath}{NotesLine(notes)}\n\nOpen it?"
                : $"{errors.Count:N0} error(s) found in {files.Count} file(s).\n\nSaved to:\n{outputPath}{NotesLine(notes)}\n\nOpen it?";

            if (MessageBox.Show(question, Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            Progress = 0;
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Progress = 0;
            StatusText = "Failed.";
            MessageBox.Show($"{ex.Message}\n\n{ex.StackTrace}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>One line for the result message: how many common values are not in the dictionary (listed on the Dictionary Notes sheet, not flagged).</summary>
    private static string NotesLine(IReadOnlyList<DictionaryNote> notes) =>
        notes.Count == 0
            ? string.Empty
            : $"\n\n{notes.Count} common value(s) are not in the dictionary: not flagged as typos, listed on the 'Dictionary Notes' sheet.";

    private bool CanStartQc() => !IsBusy && _ci != null && (_db3Files.Count + _excelFiles.Count) > 0;

    /// <summary>Pre-delivery QC: every selected db3 / excel is one batch and gets its own report next to the db3s.</summary>
    [RelayCommand(CanExecute = nameof(CanStartQc))]
    private async Task StartQcAsync()
    {
        CiData ci = _ci!;
        double limit = CaptureLimitPercent;
        DictionaryLibrary? dictionaries = _dictionaries;
        List<string> files = _db3Files.Concat(_excelFiles).ToList();
        string root = _db3Folder.Length > 0 ? _db3Folder : Path.GetDirectoryName(_excelFiles[0])!;
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        IsBusy = true;
        Progress = 0;

        List<string> reports = new();
        List<string> failures = new();
        int totalFlags = 0;

        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                string file = files[i];
                string batch = Path.GetFileName(file);
                Progress = i * 100 / files.Count;
                StatusText = $"QC {batch} ({i + 1}/{files.Count})";

                try
                {
                    QcResult result = await Task.Run(() => QcRunner.Run(file, batch, ci, limit, dictionaries), ct);

                    string report = Path.Combine(root, $"QC_{Path.GetFileNameWithoutExtension(file)}_{stamp}.xlsx");
                    for (int n = 2; File.Exists(report); n++)
                        report = Path.Combine(root, $"QC_{Path.GetFileNameWithoutExtension(file)}_{stamp}_{n}.xlsx");

                    await Task.Run(() => QcReportWriter.Write(report, result), ct);

                    reports.Add(report);
                    totalFlags += result.Flags.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{batch}: {ex.Message}");
                }
            }

            Progress = 100;
            StatusText = $"QC done - {totalFlags:N0} flag(s) in {reports.Count} batch report(s) saved in {root}";

            string text = $"{reports.Count} batch report(s) written, {totalFlags:N0} flag(s) to review.\n\nA batch is held until every flag has a Reviewer Decision.";
            if (failures.Count > 0) text += $"\n\nCould not check:\n{string.Join("\n", failures)}";

            if (reports.Count > 0 && MessageBox.Show($"{text}\n\nOpen the report folder?", Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            else if (reports.Count == 0)
                MessageBox.Show(text, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            Progress = 0;
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Progress = 0;
            StatusText = "Failed.";
            MessageBox.Show($"{ex.Message}\n\n{ex.StackTrace}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
