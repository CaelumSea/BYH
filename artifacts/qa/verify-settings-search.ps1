param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient

function Find-ByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Wait-ForAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutMs = 3000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        $element = Find-ByAutomationId -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

$process = [System.Diagnostics.Process]::Start($ExePath, '--open-settings')
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        $handle = $process.MainWindowHandle
    } while ($handle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($handle -eq [IntPtr]::Zero) {
        throw 'BYH settings window did not appear.'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $search = Find-ByAutomationId -Root $window -AutomationId 'BYH.Settings.Search.Input'
    if ($null -eq $search -or -not $search.Current.IsKeyboardFocusable) {
        throw 'Settings search input was not found or is not keyboard focusable.'
    }

    $valuePattern = [System.Windows.Automation.ValuePattern]$search.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue('clipboard privacy')
    Start-Sleep -Milliseconds 250

    # Avalonia popups may be exposed as their own HWND, so query the desktop
    # rather than restricting the result lookup to the settings window.
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $result = Wait-ForAutomationId `
        -Root $desktop `
        -AutomationId 'BYH.Settings.Search.Result.ClipboardHistory.Clipboardprivacy'
    if ($null -eq $result) {
        throw 'Search result for Clipboard privacy was not exposed.'
    }
    if (-not $result.Current.IsKeyboardFocusable) {
        throw 'Search result is not keyboard focusable.'
    }

    $invoke = [System.Windows.Automation.InvokePattern]$result.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Milliseconds 350

    $title = Find-ByAutomationId -Root $window -AutomationId 'BYH.Settings.PageTitle'
    if ($null -eq $title -or $title.Current.Name -ne 'Clipboard') {
        throw "Search navigation did not open Clipboard; got '$($title.Current.Name)'."
    }

    $target = Find-ByAutomationId -Root $window -AutomationId 'BYH.Settings.Search.Clear'
    if ($null -eq $target) {
        throw 'Clear-search control was not exposed after entering a query.'
    }

    $valuePattern.SetValue('definitely-not-a-setting')
    Start-Sleep -Milliseconds 200
    $status = Find-ByAutomationId -Root $desktop -AutomationId 'BYH.Settings.Search.Status'
    if ($null -eq $status -or $status.Current.Name -notlike 'No settings found*') {
        throw "No-result state was not exposed; got '$($status.Current.Name)'."
    }

    Write-Output 'PASS search input focusable'
    Write-Output 'PASS keyword result -> Clipboard page'
    Write-Output 'PASS result keyboard focusable'
    Write-Output 'PASS clear control exposed'
    Write-Output 'PASS no-result state exposed'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
