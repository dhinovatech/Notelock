using CommunityToolkit.Maui.Storage;
using Notelock.Services;
using System.Diagnostics;

namespace Notelock;

public partial class MainPage : ContentPage
{
    private string? _currentFilePath;
    private string? _currentPassword;
    private bool _isDirty;
    private bool _isFirstLoad = true;
    
    private double _zoomPercentage = 100.0;
    private double _baseFontSize = 14.0;
    private bool _isStatusBarVisible = true;
    private bool _isWordWrapEnabled = true;

    // Undo/Redo history variables
    private IDispatcherTimer? _undoTimer;
    private List<string> _history = new List<string>();
    private int _historyIndex = -1;
    private bool _isUndoRedoAction = false;

    public MainPage()
    {
        InitializeComponent();
        
        // Setup Undo Timer (debounce capturing)
        _undoTimer = Dispatcher.CreateTimer();
        _undoTimer.Interval = TimeSpan.FromSeconds(1);
        _undoTimer.Tick += (s, e) =>
        {
            _undoTimer.Stop();
            CaptureHistoryState(MainEditor.Text ?? string.Empty);
        };

        // Seed initial history
        _history.Add(string.Empty);
        _historyIndex = 0;
        
        MainEditor.TextChanged += (s, e) =>
        {
            if (!_isDirty)
            {
                _isDirty = true;
                UpdateTitle();
            }

            if (!_isUndoRedoAction)
            {
                _undoTimer?.Stop();
                _undoTimer?.Start();
            }
        };

        MainEditor.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(Editor.CursorPosition) || 
                e.PropertyName == nameof(Editor.SelectionLength) ||
                e.PropertyName == nameof(Editor.Text))
            {
                #if WINDOWS
                if (MainEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox nativeTextBox)
                {
                    UpdateCursorPosLabel(nativeTextBox.SelectionStart);
                    return;
                }
                #endif
                UpdateCursorPosLabel();
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_isFirstLoad)
        {
            _isFirstLoad = false;
            await CheckCommandLineArgs();
        }
        UpdateWordWrap();
        UpdateZoom();
        UpdateZoomMenuLabels();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        UpdateWordWrap();
        UpdateZoomMenuLabels();
        
        #if WINDOWS
        if (MainEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox nativeTextBox)
        {
            nativeTextBox.SelectionChanged -= OnNativeSelectionChanged;
            nativeTextBox.SelectionChanged += OnNativeSelectionChanged;
            UpdateCursorPosLabel(nativeTextBox.SelectionStart);
        }
        #endif
    }

    #if WINDOWS
    private void OnNativeSelectionChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.TextBox nativeTextBox)
        {
            UpdateCursorPosLabel(nativeTextBox.SelectionStart);
        }
    }
    #endif

    private async Task CheckCommandLineArgs()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            string fileToOpen = args[1];
            if (File.Exists(fileToOpen))
            {
                await OpenFile(fileToOpen);
            }
        }
    }

    private async void OnNewClicked(object sender, EventArgs e)
    {
        if (await PromptSaveIfDirty())
        {
            MainEditor.Text = string.Empty;
            
            // Reset undo history
            _undoTimer?.Stop();
            _history.Clear();
            _history.Add(string.Empty);
            _historyIndex = 0;

            _currentFilePath = null;
            _currentPassword = null;
            _isDirty = false;
            UpdateTitle();
        }
    }

    private void OnNewWindowClicked(object sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DisplayAlertAsync("Error", $"Could not open new window: {ex.Message}", "OK");
        }
    }

    private async void OnOpenClicked(object sender, EventArgs e)
    {
        if (!await PromptSaveIfDirty()) return;

        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Open File",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".dhin", ".txt", ".ini", ".log", ".inf", ".cfg" } },
                    { DevicePlatform.Android, new[] { "application/octet-stream", "text/plain" } },
                })
            });

            if (result != null)
            {
                await OpenFile(result.FullPath);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Failed to pick file: {ex.Message}", "OK");
        }
    }

    public async Task OpenFile(string filePath)
    {
         if (!File.Exists(filePath)) return;

        try
        {
            string ext = Path.GetExtension(filePath);
            if (ext.Equals(".dhin", StringComparison.OrdinalIgnoreCase))
            {
                string? password = await PromptPassword(isEncrypt: false);
                if (password == null) return; 

                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                
                try
                {
                    string content = EncryptionService.Decrypt(fileBytes, password);
                    
                    // Reset undo history
                    _undoTimer?.Stop();
                    _history.Clear();
                    _history.Add(content);
                    _historyIndex = 0;

                    MainEditor.Text = content;
                    _currentFilePath = filePath;
                    _currentPassword = password; // Cache the password
                    _isDirty = false;
                    UpdateTitle();
                    UpdateLineEndingsLabel();
                }
                catch (Exception)
                {
                    await DisplayAlertAsync("Error", "Decryption failed. Invalid password or corrupted file.", "OK");
                }
            }
            else
            {
                // Plain text loading
                string content = await File.ReadAllTextAsync(filePath);
                
                // Reset undo history
                _undoTimer?.Stop();
                _history.Clear();
                _history.Add(content);
                _historyIndex = 0;

                MainEditor.Text = content;
                _currentFilePath = filePath;
                _currentPassword = null; // Plain text doesn't have a cached password
                _isDirty = false;
                UpdateTitle();
                UpdateLineEndingsLabel();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Could not open file: {ex.Message}", "OK");
        }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        await PerformSave();
    }

    private async Task<bool> PerformSave()
    {
        // Force Save As if there's no file path, OR if it's not a .dhin file (since we don't save plain text)
        if (string.IsNullOrEmpty(_currentFilePath) || !Path.GetExtension(_currentFilePath).Equals(".dhin", StringComparison.OrdinalIgnoreCase))
        {
            return await PerformSaveAs();
        }
        else
        {
            return await SaveFile(_currentFilePath);
        }
    }

    private async void OnSaveAsClicked(object sender, EventArgs e)
    {
        await PerformSaveAs();
    }

    private async Task<bool> PerformSaveAs()
    {
        try 
        {
            // Prompt for password
            string? password = await PromptPassword(isEncrypt: true);
            if (password == null) return false;

            string text = MainEditor.Text ?? string.Empty;
            byte[] encrypted = EncryptionService.Encrypt(text, password);

            using var stream = new MemoryStream(encrypted);

            string initialName = "Untitled.dhin";
            if (!string.IsNullOrEmpty(_currentFilePath))
            {
                initialName = Path.ChangeExtension(Path.GetFileName(_currentFilePath), ".dhin");
            }

            var fileSaverResult = await FileSaver.Default.SaveAsync(initialName, stream, CancellationToken.None);

            if (fileSaverResult.IsSuccessful)
            {
                 _currentFilePath = fileSaverResult.FilePath;
                 _currentPassword = password; // Cache password
                 _isDirty = false;
                 UpdateTitle();
                 await DisplayAlertAsync("Success", "File saved and encrypted successfully.", "OK");
                 return true;
            }
            else 
            {
                if (fileSaverResult.Exception != null)
                     await DisplayAlertAsync("Error", $"Save failed: {fileSaverResult.Exception.Message}", "OK");
                return false;
            }
        }
        catch(Exception ex)
        {
             await DisplayAlertAsync("Error", $"An unexpected error occurred: {ex.Message}", "OK");
             return false;
        }
    }

    private async Task<bool> SaveFile(string filePath)
    {
        try
        {
            string? password = _currentPassword;
            if (password == null)
            {
                password = await PromptPassword(isEncrypt: true);
                if (password == null) return false;
            }

            string text = MainEditor.Text ?? string.Empty;
            byte[] encrypted = EncryptionService.Encrypt(text, password);
            
            await File.WriteAllBytesAsync(filePath, encrypted);
            _currentFilePath = filePath;
            _currentPassword = password; // Cache password
            _isDirty = false;
            UpdateTitle();
            
            await DisplayAlertAsync("Success", "File saved and encrypted successfully.", "OK");
            return true;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Failed to save: {ex.Message}", "OK");
            return false;
        }
    }

    private async void OnExitClicked(object sender, EventArgs e)
    {
        if (await PromptSaveIfDirty())
        {
            Application.Current?.Quit();
        }
    }

    private async Task<bool> PromptSaveIfDirty()
    {
        if (_isDirty)
        {
            string result = await DisplayActionSheetAsync("Save changes?", "Cancel", null, "Yes", "No");
            if (result == "Cancel") return false;
            if (result == "Yes")
            {
                return await PerformSave();
            }
            return true;
        }
        return true;
    }

    private Task<string?> PromptPassword(bool isEncrypt)
    {
        var tcs = new TaskCompletionSource<string?>();
        var page = new PasswordPage(tcs, isEncrypt);
        Navigation.PushModalAsync(page);
        return tcs.Task;
    }

    private void UpdateTitle()
    {
        string filename = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        string prefix = _isDirty ? "*" : "";
        Title = $"{prefix}{filename} - Notelock";
        if (StatusBarFileNameLabel != null)
        {
            StatusBarFileNameLabel.Text = $"{prefix}{filename}";
        }
    }

    // --- Edit History & Clipboard Handlers ---

    private void CaptureHistoryState(string text)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count && text == _history[_historyIndex]) return;
        
        // Remove forward history (redo states)
        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
        }
        
        _history.Add(text);
        _historyIndex = _history.Count - 1;
        
        // Cap history at 100 entries
        if (_history.Count > 100)
        {
            _history.RemoveAt(0);
            _historyIndex--;
        }
    }

    private void OnUndoClicked(object sender, EventArgs e)
    {
        if (_undoTimer != null && _undoTimer.IsRunning)
        {
            _undoTimer.Stop();
            CaptureHistoryState(MainEditor.Text ?? string.Empty);
        }
        
        if (_historyIndex > 0)
        {
            _historyIndex--;
            _isUndoRedoAction = true;
            MainEditor.Text = _history[_historyIndex];
            _isUndoRedoAction = false;
        }
    }

    private async void OnCutClicked(object sender, EventArgs e)
    {
        int start = MainEditor.CursorPosition;
        int len = MainEditor.SelectionLength;
        string text = MainEditor.Text ?? string.Empty;
        if (start >= 0 && len > 0 && start + len <= text.Length)
        {
            string selectedText = text.Substring(start, len);
            await Clipboard.Default.SetTextAsync(selectedText);
            MainEditor.Text = text.Remove(start, len);
            MainEditor.CursorPosition = start;
        }
    }

    private async void OnCopyClicked(object sender, EventArgs e)
    {
        int start = MainEditor.CursorPosition;
        int len = MainEditor.SelectionLength;
        string text = MainEditor.Text ?? string.Empty;
        if (start >= 0 && len > 0 && start + len <= text.Length)
        {
            string selectedText = text.Substring(start, len);
            await Clipboard.Default.SetTextAsync(selectedText);
        }
    }

    private async void OnPasteClicked(object sender, EventArgs e)
    {
        string clipboardText = await Clipboard.Default.GetTextAsync() ?? string.Empty;
        int start = MainEditor.CursorPosition;
        int len = MainEditor.SelectionLength;
        string text = MainEditor.Text ?? string.Empty;
        
        if (start < 0) start = 0;
        
        if (len > 0 && start + len <= text.Length)
        {
            text = text.Remove(start, len);
        }
        MainEditor.Text = text.Insert(start, clipboardText);
        MainEditor.CursorPosition = start + clipboardText.Length;
    }

    private void OnDeleteClicked(object sender, EventArgs e)
    {
        int start = MainEditor.CursorPosition;
        int len = MainEditor.SelectionLength;
        string text = MainEditor.Text ?? string.Empty;
        
        if (start < 0) return;
        
        if (len > 0 && start + len <= text.Length)
        {
            MainEditor.Text = text.Remove(start, len);
            MainEditor.CursorPosition = start;
        }
        else if (start < text.Length)
        {
            MainEditor.Text = text.Remove(start, 1);
            MainEditor.CursorPosition = start;
        }
    }

    private void OnSelectAllClicked(object sender, EventArgs e)
    {
        #if WINDOWS
        if (MainEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox nativeTextBox)
        {
            nativeTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            nativeTextBox.SelectAll();
            return;
        }
        #endif
        
        // Cross-platform fallback selection
        string text = MainEditor.Text ?? string.Empty;
        MainEditor.CursorPosition = 0;
        MainEditor.SelectionLength = text.Length;
    }

    private void OnTimeDateClicked(object sender, EventArgs e)
    {
        int start = MainEditor.CursorPosition;
        int len = MainEditor.SelectionLength;
        string text = MainEditor.Text ?? string.Empty;
        
        if (start < 0) start = 0;
        
        string timeDateStr = DateTime.Now.ToString("h:mm tt M/d/yyyy");
        if (len > 0 && start + len <= text.Length)
        {
            text = text.Remove(start, len);
        }
        
        MainEditor.Text = text.Insert(start, timeDateStr);
        MainEditor.CursorPosition = start + timeDateStr.Length;
    }

    // --- Find / Replace Event Handlers ---

    private void OnFindClicked(object sender, EventArgs e)
    {
        FindReplacePanel.IsVisible = true;
        ReplaceRow.IsVisible = false;
        FindEntry.Focus();
    }

    private void OnFindNextMenuClicked(object sender, EventArgs e)
    {
        if (!FindReplacePanel.IsVisible)
        {
            FindReplacePanel.IsVisible = true;
            ReplaceRow.IsVisible = false;
            FindEntry.Focus();
        }
        else
        {
            FindText(forward: true);
        }
    }

    private void OnFindPrevMenuClicked(object sender, EventArgs e)
    {
        if (!FindReplacePanel.IsVisible)
        {
            FindReplacePanel.IsVisible = true;
            ReplaceRow.IsVisible = false;
            FindEntry.Focus();
        }
        else
        {
            FindText(forward: false);
        }
    }

    private void OnReplaceMenuClicked(object sender, EventArgs e)
    {
        FindReplacePanel.IsVisible = true;
        ReplaceRow.IsVisible = true;
        FindEntry.Focus();
    }

    private void OnCloseFindReplaceClicked(object sender, EventArgs e)
    {
        FindReplacePanel.IsVisible = false;
        MainEditor.Focus();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        // Optional: Proactive highlight of first occurrence as user types
    }

    private void OnFindPrevClicked(object sender, EventArgs e)
    {
        FindText(forward: false);
    }

    private void OnFindNextClicked(object sender, EventArgs e)
    {
        FindText(forward: true);
    }

    private void FindText(bool forward)
    {
        string searchPattern = FindEntry.Text;
        if (string.IsNullOrEmpty(searchPattern)) return;
        
        string text = MainEditor.Text ?? string.Empty;
        int cursor = MainEditor.CursorPosition;
        int selectionLen = MainEditor.SelectionLength;
        
        StringComparison comparison = MatchCaseCheckBox.IsChecked 
            ? StringComparison.Ordinal 
            : StringComparison.OrdinalIgnoreCase;
            
        int index = -1;
        if (forward)
        {
            int searchStart = cursor + selectionLen;
            if (searchStart > text.Length) searchStart = text.Length;
            
            index = text.IndexOf(searchPattern, searchStart, comparison);
            if (index == -1 && searchStart > 0) // wrap around
            {
                index = text.IndexOf(searchPattern, 0, searchStart, comparison);
            }
        }
        else
        {
            int searchStart = cursor - 1;
            if (searchStart < 0) searchStart = text.Length - 1;
            if (searchStart >= 0 && searchStart < text.Length)
            {
                index = text.LastIndexOf(searchPattern, searchStart, comparison);
            }
            if (index == -1) // wrap around
            {
                index = text.LastIndexOf(searchPattern, text.Length - 1, comparison);
            }
        }
        
        if (index != -1)
        {
            MainEditor.CursorPosition = index;
            MainEditor.SelectionLength = searchPattern.Length;
            MainEditor.Focus();
        }
        else
        {
            DisplayAlertAsync("Find", $"Cannot find \"{searchPattern}\"", "OK");
        }
    }

    private void OnReplaceClicked(object sender, EventArgs e)
    {
        string findText = FindEntry.Text;
        if (string.IsNullOrEmpty(findText)) return;
        
        int start = MainEditor.CursorPosition;
        int len = MainEditor.SelectionLength;
        string text = MainEditor.Text ?? string.Empty;
        
        StringComparison comparison = MatchCaseCheckBox.IsChecked 
            ? StringComparison.Ordinal 
            : StringComparison.OrdinalIgnoreCase;
            
        if (len == findText.Length && start + len <= text.Length && text.Substring(start, len).Equals(findText, comparison))
        {
            string replaceText = ReplaceEntry.Text ?? string.Empty;
            MainEditor.Text = text.Remove(start, len).Insert(start, replaceText);
            MainEditor.CursorPosition = start;
            MainEditor.SelectionLength = replaceText.Length;
        }
        
        FindText(forward: true);
    }

    private void OnReplaceAllClicked(object sender, EventArgs e)
    {
        string findText = FindEntry.Text;
        if (string.IsNullOrEmpty(findText)) return;
        
        string replaceText = ReplaceEntry.Text ?? string.Empty;
        string text = MainEditor.Text ?? string.Empty;
        
        if (MatchCaseCheckBox.IsChecked)
        {
            MainEditor.Text = text.Replace(findText, replaceText);
        }
        else
        {
            var regex = new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(findText), 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            MainEditor.Text = regex.Replace(text, replaceText);
        }
        
        MainEditor.CursorPosition = 0;
        MainEditor.SelectionLength = 0;
        DisplayAlertAsync("Replace All", "All occurrences replaced.", "OK");
    }

    // --- Format / View / Zoom Event Handlers ---

    private void OnWordWrapClicked(object sender, EventArgs e)
    {
        _isWordWrapEnabled = !_isWordWrapEnabled;
        UpdateWordWrap();
    }

    private void UpdateWordWrap()
    {
        WordWrapMenuItem.Text = _isWordWrapEnabled ? "✓ Word Wrap" : "Word Wrap";
        #if WINDOWS
        if (MainEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox nativeTextBox)
        {
            nativeTextBox.TextWrapping = _isWordWrapEnabled ? Microsoft.UI.Xaml.TextWrapping.Wrap : Microsoft.UI.Xaml.TextWrapping.NoWrap;
            Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(nativeTextBox, _isWordWrapEnabled ? Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled : Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto);
        }
        #endif
    }

    private void OnZoomInClicked(object sender, EventArgs e)
    {
        _zoomPercentage = Math.Min(500, _zoomPercentage + 10);
        UpdateZoom();
    }

    private void OnZoomOutClicked(object sender, EventArgs e)
    {
        _zoomPercentage = Math.Max(10, _zoomPercentage - 10);
        UpdateZoom();
    }

    private void OnZoomResetClicked(object sender, EventArgs e)
    {
        _zoomPercentage = 100.0;
        UpdateZoom();
    }

    private void UpdateZoom()
    {
        ZoomLabel.Text = $"{_zoomPercentage}%";
        MainEditor.FontSize = _baseFontSize * (_zoomPercentage / 100.0);
    }

    private void UpdateZoomMenuLabels()
    {
        #if WINDOWS
        Dispatcher.Dispatch(() =>
        {
            if (ZoomInMenuItem.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MenuFlyoutItem nativeZoomIn)
            {
                nativeZoomIn.KeyboardAcceleratorTextOverride = "Ctrl+Plus";
            }
            if (ZoomOutMenuItem.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.MenuFlyoutItem nativeZoomOut)
            {
                nativeZoomOut.KeyboardAcceleratorTextOverride = "Ctrl+Minus";
            }
        });
        #endif
    }

    private void OnStatusBarToggleClicked(object sender, EventArgs e)
    {
        _isStatusBarVisible = !_isStatusBarVisible;
        StatusBarMenuItem.Text = _isStatusBarVisible ? "✓ Status Bar" : "Status Bar";
        StatusBar.IsVisible = _isStatusBarVisible;
    }

    private async void OnGoToClicked(object sender, EventArgs e)
    {
        string result = await DisplayPromptAsync("Go To Line", "Line number:", placeholder: "1", initialValue: "1", keyboard: Keyboard.Numeric);
        if (string.IsNullOrEmpty(result) || !int.TryParse(result, out int lineToGo) || lineToGo < 1) return;

        string text = MainEditor.Text ?? string.Empty;
        #if WINDOWS
        Microsoft.UI.Xaml.Controls.TextBox? nativeTextBox = null;
        if (MainEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            nativeTextBox = tb;
            text = tb.Text ?? string.Empty;
        }
        #endif
        
        int currentLine = 1;
        int charIndex = 0;
        
        if (lineToGo > 1)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    currentLine++;
                    if (currentLine == lineToGo)
                    {
                        charIndex = i + 1;
                        if (i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            charIndex = i + 2;
                        }
                        break;
                    }
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++; 
                    }
                }
                else if (c == '\n')
                {
                    currentLine++;
                    if (currentLine == lineToGo)
                    {
                        charIndex = i + 1;
                        break;
                    }
                }
            }
            
            if (currentLine < lineToGo)
            {
                await DisplayAlertAsync("Go To Line", "The line number is beyond the total number of lines.", "OK");
                return;
            }
        }
        
        #if WINDOWS
        if (nativeTextBox != null)
        {
            nativeTextBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            nativeTextBox.Select(charIndex, 0);
            return;
        }
        #endif

        MainEditor.CursorPosition = charIndex;
        MainEditor.SelectionLength = 0;
        MainEditor.Focus();
    }

    private void OnAboutClicked(object sender, EventArgs e)
    {
        DisplayAlertAsync("About Notelock", "Notelock Secure Text Editor\nVersion 2.0\n\nFeatures:\n- Windows 10 Notepad compatibility\n- AES-256 Encrypted Documents (.dhin)\n- Premium Security UI", "OK");
    }

    // --- Status Bar Helper Methods ---

    private void UpdateCursorPosLabel(int? nativeCursorPos = null)
    {
        string text = MainEditor.Text ?? string.Empty;
        #if WINDOWS
        if (MainEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox nativeTextBox)
        {
            text = nativeTextBox.Text ?? string.Empty;
        }
        #endif
        
        int cursor;
        if (nativeCursorPos.HasValue)
        {
            cursor = nativeCursorPos.Value;
        }
        else
        {
            cursor = MainEditor.CursorPosition;
        }
        
        cursor = Math.Clamp(cursor, 0, text.Length);
        
        int line = 1;
        int col = 1;
        
        for (int i = 0; i < cursor; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                line++;
                col = 1;
                if (i + 1 < cursor && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }
        
        CursorPosLabel.Text = $"Ln {line}, Col {col}";
    }

    private void UpdateLineEndingsLabel()
    {
        string text = MainEditor.Text ?? string.Empty;
        if (text.Contains("\r\n"))
        {
            LineEndingsLabel.Text = "Windows (CRLF)";
        }
        else if (text.Contains("\n"))
        {
            LineEndingsLabel.Text = "Unix (LF)";
        }
        else
        {
            LineEndingsLabel.Text = "Windows (CRLF)";
        }
    }
}
