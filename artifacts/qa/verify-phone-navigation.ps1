param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient

$pages = @(
    @{ Button = 'BYH.Settings.PhoneNav.General';     View = 'BYH.Settings.PhoneView.Overview' },
    @{ Button = 'BYH.Settings.PhoneNav.Translation'; View = 'BYH.Settings.PhoneView.Translation' },
    @{ Button = 'BYH.Settings.PhoneNav.Vision';      View = 'BYH.Settings.PhoneView.Vision' },
    @{ Button = 'BYH.Settings.PhoneNav.Clipboard';   View = 'BYH.Settings.PhoneView.Clipboard' },
    @{ Button = 'BYH.Settings.PhoneNav.Launcher';    View = 'BYH.Settings.PhoneView.Launcher' }
)

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
    $windowPattern = $window.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    ([System.Windows.Automation.WindowPattern]$windowPattern).SetWindowVisualState(
        [System.Windows.Automation.WindowVisualState]::Maximized)
    Start-Sleep -Milliseconds 350
    $title = Find-ByAutomationId -Root $window -AutomationId 'BYH.Settings.PageTitle'
    if ($null -eq $title -or $title.Current.Name -ne 'General') {
        throw "Expected central page General before phone navigation; got '$($title.Current.Name)'."
    }

    foreach ($page in $pages) {
        $button = Find-ByAutomationId -Root $window -AutomationId $page.Button
        if ($null -eq $button) {
            throw "Phone button '$($page.Button)' was not found."
        }
        if (-not $button.Current.IsKeyboardFocusable) {
            throw "Phone button '$($page.Button)' is not keyboard focusable."
        }

        $pattern = $button.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        Start-Sleep -Milliseconds 180

        $view = Find-ByAutomationId -Root $window -AutomationId $page.View
        if ($null -eq $view) {
            throw "Phone view '$($page.View)' was not exposed after invocation."
        }
        $bounds = $view.Current.BoundingRectangle
        if ($bounds.Width -le 0 -or $bounds.Height -le 0) {
            throw "Phone view '$($page.View)' has empty bounds after invocation."
        }

        $title = Find-ByAutomationId -Root $window -AutomationId 'BYH.Settings.PageTitle'
        if ($title.Current.Name -ne 'General') {
            throw "Phone navigation changed the central page to '$($title.Current.Name)'."
        }

        Write-Output "PASS $($page.Button) -> $($page.View); central=General; focusable=True; bounds=$([int]$bounds.Width)x$([int]$bounds.Height)"
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
