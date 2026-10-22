param(
    [Parameter(Mandatory)] [ValidateSet("list", "shot", "click", "tab", "select", "settext", "type", "close", "controls",
        "combos", "retype", "timing", "dialogs")] [string]$Action,
    [string]$Title = "",     # part of the window's title
    [string]$Name = "",      # the control's name (button text, tab title, first column of a list row)
    [string]$Out = "",
    [string]$Value = "",
    [int]$Index = 0,
    [string]$Process = "Lemur"
)
# Drives and captures Lemur's windows for the manual. Captures with PrintWindow: only that window's own pixels,
# whatever is in front of it (never the screen).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, System.Text.StringBuilder l);
  public static string Text(IntPtr h) { var sb = new System.Text.StringBuilder(2048); SendMessage(h, 0x000D, (IntPtr)2048, sb); return sb.ToString(); }
  public static string Class(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
  // Every child window whose class contains `part` (any case), in the order Windows lists them: for a form, its layout order.
  public static System.Collections.Generic.List<IntPtr> Children(IntPtr parent, string part) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumChildWindows(parent, (h, l) => { if (Class(h).ToUpperInvariant().Contains(part)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  // A combo box's list: CB_GETCOUNT, CB_GETLBTEXTLEN, CB_GETLBTEXT (Windows copies the text across processes).
  public static System.Collections.Generic.List<string> Items(IntPtr h) {
    var items = new System.Collections.Generic.List<string>();
    int n = (int)SendMessage(h, 0x0146, IntPtr.Zero, IntPtr.Zero);
    for (int i = 0; i < n; i++) {
      int len = (int)SendMessage(h, 0x0149, (IntPtr)i, IntPtr.Zero);
      var sb = new System.Text.StringBuilder(len + 1);
      SendMessage(h, 0x0148, (IntPtr)i, sb);
      items.Add(sb.ToString());
    }
    return items;
  }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  // The visible top-level windows of these processes, owned ones included.
  public static System.Collections.Generic.List<IntPtr> TopLevel(int[] pids) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (IsWindowVisible(h) && Array.IndexOf(pids, (int)p) >= 0) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static IntPtr FindChildByClass(IntPtr parent, string part) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, (h, l) => { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256);
      if (sb.ToString().Contains(part)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
[W]::SetProcessDPIAware() | Out-Null
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

# These read what a window holds (names in lists, dialog text) or type over it, so they only ever run on the demo copy.
if (@("combos", "retype", "timing", "dialogs") -contains $Action -and $Process -ne "LemurDemo") {
    Write-Output "$Action is for the demo only (-Process LemurDemo)"; exit 1
}

$pids = @(Get-Process -Name $Process -ErrorAction SilentlyContinue | ForEach-Object Id)
if ($pids.Count -eq 0) { Write-Output "no $Process process"; exit 1 }
$windows = @($A::RootElement.FindAll($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition) |
    Where-Object { $pids -contains $_.Current.ProcessId })
# A dialog shown with an owner (ShowDialog) sits under its owner window in the automation tree.
$windowType = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$windows += @($windows | ForEach-Object { $_.FindAll($Scope::Children, $windowType) })
# One opened while its app wasn't the active one (Settings… clicked with a posted message) is owned by a hidden window,
# so it isn't in that tree at all: it's found by its handle.
$known = @($windows | ForEach-Object { $_.Current.NativeWindowHandle })
foreach ($h in [W]::TopLevel([int[]]$pids)) {
    if ($known -notcontains [int]$h) { try { $windows += $A::FromHandle($h) } catch { } }
}

if ($Action -eq "list") { $windows | ForEach-Object { "{0}  [{1}]" -f $_.Current.Name, $_.Current.NativeWindowHandle }; exit 0 }

if ($Action -eq "dialogs") {
    # Message boxes (class #32770): prints each one's title and text, then presses its first button.
    foreach ($d in $windows | Where-Object { [W]::Class([IntPtr]$_.Current.NativeWindowHandle) -eq "#32770" }) {
        $h = [IntPtr]$d.Current.NativeWindowHandle
        $texts = [W]::Children($h, "STATIC") | ForEach-Object { [W]::Text($_) } | Where-Object { $_ }
        "dialog '{0}': {1}" -f $d.Current.Name, ($texts -join " / ")
        $ok = [W]::Children($h, "BUTTON") | Select-Object -First 1
        if ($ok) { [W]::PostMessage($ok, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; "  pressed '{0}'" -f [W]::Text($ok) }
    }
    exit 0
}

$win = $windows | Where-Object { $_.Current.Name -like "*$Title*" } | Select-Object -First 1
if (-not $win) { Write-Output "window '$Title' not found"; exit 1 }

function Find-Control([System.Windows.Automation.ControlType]$type, [string]$name) {
    # Names are matched from the start, so "Name speakers" finds "Name speakers…" (the ellipsis doesn't survive the shell).
    $all = $win.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type)))
    $exact = $all | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if ($exact) { return $exact }
    return $all | Where-Object { $_.Current.Name -like "$name*" } | Select-Object -First 1
}

switch ($Action) {
    "shot" {
        $h = [IntPtr]$win.Current.NativeWindowHandle
        $r = New-Object W+RECT
        [W]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [W]::PrintWindow($h, $hdc, 2) | Out-Null
        $g.ReleaseHdc($hdc)
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "saved $Out $($bmp.Width)x$($bmp.Height)"
    }
    "click" {
        $c = Find-Control $CT::Button $Name
        if (-not $c) { $c = Find-Control $CT::Hyperlink $Name }
        if (-not $c) { $c = Find-Control $CT::RadioButton $Name }
        if (-not $c) { $c = Find-Control $CT::CheckBox $Name }
        if (-not $c) { $c = Find-Control $CT::Pane $Name }   # Windows Installer's own dialogs
        if (-not $c) { Write-Output "no button '$Name'"; exit 1 }
        # BM_CLICK, posted: a button that opens a modal dialog would hold a UI Automation Invoke until it closes.
        $h = [IntPtr]$c.Current.NativeWindowHandle
        if ($h -ne [IntPtr]::Zero) { [W]::PostMessage($h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        else { $c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        Write-Output "clicked $Name"
    }
    "tab" {
        # TCM_SETCURFOCUS to the tab control by index: it raises the same change notifications as a click
        # (UI Automation's Select hangs inside a modal dialog).
        $tabs = [W]::FindChildByClass([IntPtr]$win.Current.NativeWindowHandle, "SysTabControl32")
        if ($tabs -eq [IntPtr]::Zero) { Write-Output "no tab control"; exit 1 }
        [W]::PostMessage($tabs, 0x1330, [IntPtr]$Index, [IntPtr]::Zero) | Out-Null
        Write-Output "tab $Index"
    }
    "select" {
        $c = Find-Control $CT::ListItem $Name
        if (-not $c) {
            # A list row's name is its first column; match on any column instead.
            $items = $win.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::ListItem)))
            $c = $items | Where-Object { $_.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like "*$Name*" } } | Select-Object -First 1
        }
        if (-not $c) { Write-Output "no row '$Name'"; exit 1 }
        $c.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Write-Output "selected $Name"
    }
    "settext" {
        $edits = @($win.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Edit))))
        if ($Index -ge $edits.Count) { Write-Output "only $($edits.Count) edit boxes"; exit 1 }
        $edits[$Index].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
        Write-Output "edit $Index set"
    }
    "type" {
        # Keys typed at the end of edit box n, as posted WM_CHAR messages: the box raises its change events, which a UI
        # Automation SetValue doesn't (a button that waits for typing stays grey). `b is backspace. No focus needed.
        $edits = @($win.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Edit))))
        if ($Index -ge $edits.Count) { Write-Output "only $($edits.Count) edit boxes"; exit 1 }
        $h = [IntPtr]$edits[$Index].Current.NativeWindowHandle
        [W]::SendMessage($h, 0x00B1, [IntPtr](-1), [IntPtr](-1)) | Out-Null   # EM_SETSEL: caret at the end
        foreach ($ch in $Value.ToCharArray()) { [W]::PostMessage($h, 0x0102, [IntPtr][int]$ch, [IntPtr]1) | Out-Null }
        Write-Output "typed into edit $Index"
    }
    "controls" {
        $win.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
            "{0,-14} {1}" -f $_.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""), $_.Current.Name
        } | Where-Object { $_ -notmatch "^\S+\s*$" } | Select-Object -First 120
    }
    "combos" {
        # Every combo box in the window: its text, then its list.
        $i = 0
        foreach ($h in [W]::Children([IntPtr]$win.Current.NativeWindowHandle, "COMBOBOX")) {
            "box {0}: '{1}'  list: {2}" -f $i, [W]::Text($h), ([W]::Items($h) -join " | ")
            $i++
        }
    }
    { $_ -in "retype", "timing" } {
        # Keys posted to combo box n's edit part, as a person types (its suggestions can fill in the rest). retype empties
        # the box first; timing adds to the end and reports how long the window took to take every key in.
        $combo = [W]::Children([IntPtr]$win.Current.NativeWindowHandle, "COMBOBOX")[$Index]
        if (-not $combo) { Write-Output "no combo box $Index"; exit 1 }
        $edit = [W]::Children($combo, "EDIT")[0]
        if ($Action -eq "retype") {
            [W]::SendMessage($edit, 0x00B1, [IntPtr]0, [IntPtr](-1)) | Out-Null    # EM_SETSEL: everything
            [W]::PostMessage($edit, 0x0102, [IntPtr]8, [IntPtr]1) | Out-Null        # backspace
            $want = $null
        } else {
            [W]::SendMessage($edit, 0x00B1, [IntPtr](-1), [IntPtr](-1)) | Out-Null  # EM_SETSEL: caret at the end
            $want = [W]::Text($combo) + $Value
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        foreach ($ch in $Value.ToCharArray()) { [W]::PostMessage($edit, 0x0102, [IntPtr][int]$ch, [IntPtr]1) | Out-Null }
        if ($Action -eq "retype") { Start-Sleep -Milliseconds 1500; "box {0} says '{1}'" -f $Index, [W]::Text($combo) }
        else {
            # Done when the box starts with every key typed (a suggestion may have filled in the rest).
            while (-not [W]::Text($combo).StartsWith($want, [StringComparison]::Ordinal) -and $sw.ElapsedMilliseconds -lt 30000) { Start-Sleep -Milliseconds 5 }
            "{0} keys in {1} ms ({2:0} ms a key); box {3} says '{4}'" -f $Value.Length, $sw.ElapsedMilliseconds,
                ($sw.ElapsedMilliseconds / [Math]::Max(1, $Value.Length)), $Index, [W]::Text($combo)
        }
    }
    "close" {
        [W]::PostMessage([IntPtr]$win.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Write-Output "closed $Title"
    }
}
