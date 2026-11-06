Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Speech

# Озвучка
try {
    $speech = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $speech.Speak("The computer will be put into hibernation mode in 10 seconds.")
} catch {
    Write-Host "Speech failed, continuing..."
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "Hibernation Countdown"
$form.Size = New-Object System.Drawing.Size(300, 200)
$form.StartPosition = "CenterScreen"
$form.TopMost = $true

$label = New-Object System.Windows.Forms.Label
$label.Text = "Hibernating in 10..."
$label.Size = New-Object System.Drawing.Size(250, 40)
$label.Location = New-Object System.Drawing.Point(25, 30)
$label.TextAlign = "MiddleCenter"
$label.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($label)

$cancelBtn = New-Object System.Windows.Forms.Button
$cancelBtn.Text = "CANCEL"
$cancelBtn.Location = New-Object System.Drawing.Point(100, 90)
$cancelBtn.Size = New-Object System.Drawing.Size(100, 40)
$cancelBtn.BackColor = [System.Drawing.Color]::DarkRed
$cancelBtn.ForeColor = [System.Drawing.Color]::White
$cancelBtn.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$cancelBtn.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$form.Controls.Add($cancelBtn)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$script:count = 10

$timer.Add_Tick({
    $script:count--
    if ($script:count -gt 0) {
        $label.Text = "Hibernating in $script:count..."
        if ($script:count -le 3) {
            $label.ForeColor = [System.Drawing.Color]::Red
            [System.Media.SystemSounds]::Beep.Play()
        }
    } else {
        $timer.Stop()
        $form.Close()
        Write-Host "Triggering hibernation..."
        # Самый надежный способ вызвать гибернацию в Windows через API
        $setSuspendState = Add-Type -MemberDefinition @"
[DllImport("powrprof.dll", SetLastError = true)]
public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
"@ -Name "PowerManagement" -Namespace "Win32" -PassThru

        $setSuspendState::SetSuspendState($true, $false, $false)
    }
})

$timer.Start()
$result = $form.ShowDialog()

if ($result -eq [System.Windows.Forms.DialogResult]::Cancel) {
    $timer.Stop()
    Write-Host "Hibernation CANCELLED by user."
    exit
}
