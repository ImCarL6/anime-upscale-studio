param(
    [string]$DllPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'app\Anime4KEncoder.dll')
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) {
    throw 'Execute este teste em STA: pwsh -Sta -File .\Test-LegacyProfilesUi.ps1'
}
if (-not (Test-Path -LiteralPath $DllPath)) { throw "Aplicativo publicado não encontrado: $DllPath" }

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore

$projectRoot = Split-Path (Split-Path $DllPath -Parent) -Parent
$previousLocation = Get-Location
$window = $null

try {
    Set-Location -LiteralPath $projectRoot
    $assembly = [Reflection.Assembly]::LoadFrom($DllPath)
    $windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
    $window = [Activator]::CreateInstance($windowType)

    $engineCombo = $window.FindName('EngineCombo')
    $profileCombo = $window.FindName('ProfileCombo')
    $warningPanel = $window.FindName('LegacyProfileWarningPanel')
    $warningText = $window.FindName('LegacyProfileWarningText')
    $startButton = $window.FindName('StartButton')
    $pilotButton = $window.FindName('PilotButton')
    $pilotPanel = $window.FindName('PilotSummaryPanel')
    $pilotSummary = $window.FindName('PilotSummaryText')
    $openPilotButton = $window.FindName('OpenPilotButton')
    $footerText = $window.FindName('FooterText')
    if ($null -eq $engineCombo -or $null -eq $profileCombo -or $null -eq $warningPanel -or $null -eq $warningText -or
        $null -eq $startButton -or $null -eq $pilotButton -or $null -eq $pilotPanel -or $null -eq $pilotSummary -or
        $null -eq $openPilotButton -or $null -eq $footerText) {
        throw 'Os controles de perfil, aviso e piloto não foram encontrados no XAML publicado.'
    }
    if (-not $startButton.IsEnabled) { throw 'O ambiente publicado não reconheceu todos os modelos FP16 necessários.' }
    if (-not $pilotButton.IsEnabled) { throw 'O botão de piloto não foi habilitado no ambiente publicado.' }
    if ($pilotPanel.Visibility -ne [Windows.Visibility]::Collapsed) { throw 'O resultado do piloto deveria iniciar recolhido.' }
    if ($footerText.Text -notmatch 'fingerprint') { throw 'A interface não informa o isolamento dos blocos por fingerprint.' }

    $engineCombo.SelectedIndex = 1
    $window.Dispatcher.Invoke([Action]{})
    if ($engineCombo.SelectedItem.Name -ne 'AnimeJaNai') { throw 'Não foi possível selecionar o motor AnimeJaNai.' }

    $defaultProfile = $profileCombo.SelectedItem.Name
    if ($defaultProfile -ne 'V3.1 Balanced') { throw "Perfil padrão inseguro: $defaultProfile" }

    $expectedProfiles = @(
        'V3 Compact FP16 (Legacy/Experimental)',
        'V3 Compact Sharp1 FP16 (Legacy/Experimental)',
        'V3.1 Balanced',
        'V3.1 Balanced Sharp1',
        'V3.1 Performance',
        'V3.1 Performance Sharp1'
    )
    $actualProfiles = @($profileCombo.Items | ForEach-Object { $_.Name })
    if (($actualProfiles -join '|') -ne ($expectedProfiles -join '|')) {
        throw "Lista inesperada: $($actualProfiles -join ', ')"
    }
    $legacyProfiles = @($profileCombo.Items | Where-Object { $_.IsLegacyExperimental })
    if ($legacyProfiles.Count -ne 2) { throw "Quantidade inesperada de perfis legados: $($legacyProfiles.Count)" }
    if ($legacyProfiles[0].Code -ne 'V3-Compact' -or $legacyProfiles[0].ModelName -ne '2x_AnimeJaNai_HD_V3_Compact_strong_fp16' -or
        $legacyProfiles[1].Code -ne 'V3-Compact-Sharp1' -or $legacyProfiles[1].ModelName -ne '2x_AnimeJaNai_HD_V3Sharp1_Compact_strong_fp16') {
        throw 'Os identificadores persistidos ou os arquivos ONNX dos perfis legados foram alterados.'
    }

    foreach ($legacyIndex in 0, 1) {
        $profileCombo.SelectedIndex = $legacyIndex
        $window.Dispatcher.Invoke([Action]{})
        if ($warningPanel.Visibility -ne [Windows.Visibility]::Visible) {
            throw "O aviso persistente não ficou visível para $($profileCombo.SelectedItem.Name)."
        }
        if ($warningText.Text -notmatch 'FP32' -or $warningText.Text -notmatch 'FP16' -or $warningText.Text -notmatch '120 quadros' -or $warningText.Text -notmatch 'sem corrupção') {
            throw "O aviso não descreve o risco técnico esperado: $($warningText.Text)"
        }
    }

    $profileCombo.SelectedIndex = 2
    $window.Dispatcher.Invoke([Action]{})
    if ($warningPanel.Visibility -ne [Windows.Visibility]::Collapsed) {
        throw 'O aviso legado permaneceu visível em um perfil V3.1 seguro.'
    }

    Write-Output 'UI_VALIDATION=PASS'
    Write-Output "DEFAULT_PROFILE=$defaultProfile"
    Write-Output "PROFILES=$($actualProfiles -join ' | ')"
    Write-Output 'LEGACY_WARNING=VISIBLE_FOR_COMPACT_AND_COLLAPSED_FOR_V3.1'
    Write-Output 'PILOT_UI=AVAILABLE_AND_COLLAPSED_UNTIL_MEASURED'
    Write-Output 'TOOLCHAIN_CACHE_UI=FINGERPRINT_NAMESPACED'
}
finally {
    if ($null -ne $window) { $window.Close() }
    Set-Location -LiteralPath $previousLocation
}
