using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoltCore
{
    /// <summary>Todos os ajustes do app. Página "debloat" ou "perf".</summary>
    public static class Catalog
    {
        private const string HKLM_POL = @"HKLM\SOFTWARE\Policies\Microsoft\Windows";
        private const string HKCU_POL = @"HKCU\Software\Policies\Microsoft\Windows";
        private const string CDM = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        private const string ADV = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string EDGE_POL = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";
        private const string MMCSS = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string SVC = @"HKLM\SYSTEM\CurrentControlSet\Services\";

        private static List<Tweak> _all;

        public static List<Tweak> All
        {
            get { if (_all == null) _all = Build(); return _all; }
        }

        public static IEnumerable<Tweak> Page(string page) { return All.Where(t => t.Page == page); }

        public static Tweak ById(string id) { return All.FirstOrDefault(t => t.Id == id); }

        private static Tweak T(string page, string group, string id, Level lv, string title, string desc)
        {
            return new Tweak { Page = page, Group = group, Id = id, Level = lv, Title = title, Desc = desc };
        }

        // ---------- Remoção de apps (AppX) ----------
        private static Tweak App(string group, string id, Level lv, string title, string desc, string storeName, params string[] patterns)
        {
            var t = T("debloat", group, id, lv, title, desc);
            var parts = new List<string>();
            foreach (var p in patterns)
            {
                parts.Add("Get-AppxPackage -AllUsers -Name " + Shell.Q(p) + " | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue }");
                parts.Add("Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like " + Shell.Q(p) + " } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null }");
            }
            t.ApplyPs.Add(string.Join("; ", parts));
            t.CustomApply = () => TweakEngine.RefreshAppx();
            t.Check = () => !patterns.Any(TweakEngine.AnyAppxLike);
            t.CustomUndo = () => Shell.Open("ms-windows-store://search/?query=" + Uri.EscapeDataString(storeName));
            t.UndoNote = "Desfazer abre a Microsoft Store para reinstalar.";
            return t;
        }

        private static List<Tweak> Build()
        {
            var L = new List<Tweak>();

            // ===================== DEBLOAT =====================
            L.Add(T("debloat", "IA e Copilot", "copilot", Level.Essencial, "Copilot",
                "Desativa o Copilot do Windows por política, esconde o botão da barra de tarefas e remove o app Copilot.")
                .Reg(HKCU_POL + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1)
                .Reg(HKLM_POL + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1)
                .Reg(ADV, "ShowCopilotButton", 0)
                .Reg(EDGE_POL, "HubsSidebarEnabled", 0)
                .Ps("Get-AppxPackage -AllUsers | Where-Object { $_.Name -like '*Copilot*' } | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue }; Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like '*Copilot*' } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null }", null)
                .Note("O app Copilot pode ser reinstalado pela Microsoft Store."));

            L.Add(T("debloat", "IA e Copilot", "recall", Level.Essencial, "Recall e Click to Do",
                "Impede o Windows de tirar capturas da sua tela (Recall) e desativa o Click to Do e a análise de IA.")
                .Reg(HKLM_POL + @"\WindowsAI", "DisableAIDataAnalysis", 1)
                .Reg(HKLM_POL + @"\WindowsAI", "AllowRecallEnablement", 0)
                .Reg(HKCU_POL + @"\WindowsAI", "DisableAIDataAnalysis", 1)
                .Reg(HKLM_POL + @"\WindowsAI", "DisableClickToDo", 1)
                .Reg(HKCU_POL + @"\WindowsAI", "DisableClickToDo", 1));

            L.Add(T("debloat", "IA e Copilot", "ai_apps", Level.Recomendado, "IA no Paint, Bloco de Notas e Edge",
                "Desliga os recursos de IA generativa embutidos no Paint e no Bloco de Notas e o Copilot dentro do Edge.")
                .Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1)
                .Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1)
                .Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1)
                .Reg(@"HKLM\SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)
                .Reg(EDGE_POL, "CopilotPageContext", 0)
                .Reg(EDGE_POL, "CopilotCDPPageContext", 0));

            L.Add(T("debloat", "Barra de tarefas e Início", "widgets", Level.Essencial, "Widgets (notícias e clima)",
                "Desliga o painel de Widgets/Notícias, que fica rodando em segundo plano e consumindo memória.")
                .Reg(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)
                .Reg(HKLM_POL + @"\Windows Feeds", "EnableFeeds", 0));

            L.Add(T("debloat", "Barra de tarefas e Início", "bing_search", Level.Essencial, "Bing na pesquisa do Iniciar",
                "A pesquisa do Iniciar passa a buscar só no seu PC, sem enviar o que você digita para o Bing.")
                .Reg(HKCU_POL + @"\Explorer", "DisableSearchBoxSuggestions", 1)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)
                .Reg(HKLM_POL + @"\Windows Search", "ConnectedSearchUseWeb", 0)
                .Reg(HKLM_POL + @"\Windows Search", "AllowCloudSearch", 0));

            L.Add(T("debloat", "Barra de tarefas e Início", "start_recs", Level.Recomendado, "Recomendações do Iniciar",
                "Remove as sugestões de apps e sites da seção Recomendados do Iniciar.")
                .Reg(ADV, "Start_IrisRecommendations", 0)
                .Reg(ADV, "Start_AccountNotifications", 0)
                .Reg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1));

            L.Add(T("debloat", "Telemetria e privacidade", "telemetry", Level.Essencial, "Telemetria do Windows",
                "Reduz a coleta de dados ao mínimo, desliga o serviço DiagTrack e as tarefas agendadas de telemetria.")
                .Reg(HKLM_POL + @"\DataCollection", "AllowTelemetry", 0)
                .Reg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0)
                .Reg(HKLM_POL + @"\DataCollection", "DoNotShowFeedbackNotifications", 1)
                .Reg(SVC + "DiagTrack", "Start", 4)
                .Reg(SVC + "dmwappushservice", "Start", 4)
                .Reg(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)
                .Ps(TasksPs(true), TasksPs(false))
                .Boost());

            L.Add(T("debloat", "Telemetria e privacidade", "adid", Level.Essencial, "ID de publicidade e experiências personalizadas",
                "Impede que apps usem seu ID de anúncio e que a Microsoft use seus dados de diagnóstico para anúncios.")
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)
                .Reg(HKLM_POL + @"\AdvertisingInfo", "DisabledByGroupPolicy", 1)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)
                .Reg(HKCU_POL + @"\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1));

            L.Add(T("debloat", "Telemetria e privacidade", "activity", Level.Recomendado, "Histórico de atividades e localização para anúncios",
                "Para de registrar o histórico de atividades e de enviar dados de digitação e escrita à Microsoft.")
                .Reg(HKLM_POL + @"\System", "PublishUserActivities", 0)
                .Reg(HKLM_POL + @"\System", "UploadUserActivities", 0)
                .Reg(HKLM_POL + @"\System", "EnableActivityFeed", 0)
                .Reg(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1)
                .Reg(@"HKCU\Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1)
                .Reg(@"HKCU\Software\Microsoft\Input\TIPC", "Enabled", 0));

            L.Add(T("debloat", "Propagandas", "ads", Level.Essencial, "Propagandas e apps instalados sozinhos",
                "Bloqueia sugestões, dicas, propagandas na tela de bloqueio e a instalação automática de apps patrocinados (Candy Crush, TikTok etc.).")
                .Reg(CDM, "SilentInstalledAppsEnabled", 0)
                .Reg(CDM, "SystemPaneSuggestionsEnabled", 0)
                .Reg(CDM, "SoftLandingEnabled", 0)
                .Reg(CDM, "RotatingLockScreenOverlayEnabled", 0)
                .Reg(CDM, "PreInstalledAppsEnabled", 0)
                .Reg(CDM, "OemPreInstalledAppsEnabled", 0)
                .Reg(CDM, "SubscribedContent-338388Enabled", 0)
                .Reg(CDM, "SubscribedContent-338389Enabled", 0)
                .Reg(CDM, "SubscribedContent-338393Enabled", 0)
                .Reg(CDM, "SubscribedContent-353694Enabled", 0)
                .Reg(CDM, "SubscribedContent-353696Enabled", 0)
                .Reg(CDM, "SubscribedContent-310093Enabled", 0)
                .Reg(HKLM_POL + @"\CloudContent", "DisableWindowsConsumerFeatures", 1)
                .Reg(HKCU_POL + @"\CloudContent", "DisableWindowsSpotlightFeatures", 1)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)
                .Boost());

            L.Add(T("debloat", "Propagandas", "explorer_ads", Level.Recomendado, "Anúncios no Explorador e nas Configurações",
                "Esconde as propagandas do OneDrive/Microsoft 365 no Explorador de Arquivos e as sugestões no app Configurações.")
                .Reg(ADV, "ShowSyncProviderNotifications", 0)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0)
                .Reg(CDM, "SubscribedContent-338387Enabled", 0));

            L.Add(T("debloat", "Microsoft Edge", "edge_bg", Level.Essencial, "Edge rodando em segundo plano",
                "Impede o Edge de iniciar junto com o Windows (Startup Boost) e de continuar aberto depois de fechado. Também remove compras, barra lateral e recomendações.")
                .Reg(EDGE_POL, "StartupBoostEnabled", 0)
                .Reg(EDGE_POL, "BackgroundModeEnabled", 0)
                .Reg(EDGE_POL, "EdgeShoppingAssistantEnabled", 0)
                .Reg(EDGE_POL, "ShowRecommendationsEnabled", 0)
                .Reg(EDGE_POL, "PersonalizationReportingEnabled", 0)
                .Reg(EDGE_POL, "HideFirstRunExperience", 1)
                .Reg(EDGE_POL, "DefaultBrowserSettingEnabled", 0)
                .Boost());

            AddServices(L, "svc_diag", Level.Recomendado, "Relatórios de erro e diagnóstico",
                "Desliga o envio de relatórios de erro à Microsoft, o coletor do Hub de Diagnóstico e o modo demonstração de loja.",
                "WerSvc", "diagnosticshub.standardcollector.service", "RetailDemo", "wisvc");
            AddServices(L, "svc_misc", Level.Recomendado, "Mapas offline, Fax e Registro Remoto",
                "Serviços que quase ninguém usa num PC gamer: download de mapas offline, fax e edição remota do registro.",
                "MapsBroker", "Fax", "RemoteRegistry");
            AddServices(L, "svc_print", Level.Avancado, "Spooler de impressão",
                "Desligue SOMENTE se você não usa impressora (nem 'Imprimir para PDF').",
                "Spooler", "PrintNotify");
            AddServices(L, "svc_xbox", Level.Avancado, "Serviços Xbox Live",
                "Desligue SOMENTE se você não joga pelo app Xbox / Game Pass / Microsoft Store. Controles Xbox continuam funcionando.",
                "XblAuthManager", "XblGameSave", "XboxNetApiSvc");

            var edgeExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
            L.Add(T("debloat", "Microsoft Edge", "edge_remove", Level.Avancado, "Desinstalar o Microsoft Edge",
                "Remove o navegador Edge. O WebView2 (usado por outros apps) é mantido. Tenha outro navegador instalado antes. Em algumas versões fora da Europa a Microsoft bloqueia a remoção — nesse caso nada é alterado.")
                .Ps(@"Get-Process msedge,MicrosoftEdgeUpdate -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$k='HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdateDev'; New-Item $k -Force | Out-Null; New-ItemProperty $k -Name AllowUninstall -Value '' -PropertyType String -Force | Out-Null
New-Item 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate' -Force | Out-Null; New-ItemProperty 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate' -Name DoNotUpdateToEdgeWithChromium -Value 1 -PropertyType DWord -Force | Out-Null
$s = Get-ChildItem ""${env:ProgramFiles(x86)}\Microsoft\Edge\Application\*\Installer\setup.exe"" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($s) { Start-Process $s.FullName -ArgumentList '--uninstall --system-level --verbose-logging --force-uninstall' -Wait }
Remove-Item ""$env:PUBLIC\Desktop\Microsoft Edge.lnk"",""$env:USERPROFILE\Desktop\Microsoft Edge.lnk"" -ErrorAction SilentlyContinue",
                    "Remove-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\EdgeUpdate' -Name DoNotUpdateToEdgeWithChromium -ErrorAction SilentlyContinue; winget install -e --id Microsoft.Edge --silent --accept-package-agreements --accept-source-agreements")
                .Checked(() => !File.Exists(edgeExe))
                .Note("Desfazer reinstala o Edge pelo winget."));

            var odUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\OneDrive\OneDrive.exe");
            var odMachine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft OneDrive\OneDrive.exe");
            L.Add(T("debloat", "Apps da Microsoft", "onedrive", Level.Avancado, "OneDrive",
                "Desinstala o OneDrive, que sincroniza arquivos em segundo plano e abre com o Windows. Seus arquivos na nuvem continuam salvos online.")
                .Ps(@"Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue
$p = ""$env:SystemRoot\System32\OneDriveSetup.exe""; if (-not (Test-Path $p)) { $p = ""$env:SystemRoot\SysWOW64\OneDriveSetup.exe"" }
winget uninstall -e --id Microsoft.OneDrive --silent --accept-source-agreements | Out-Null
if (Test-Path $p) { Start-Process $p -ArgumentList '/uninstall' -Wait }",
                    "winget install -e --id Microsoft.OneDrive --silent --accept-package-agreements --accept-source-agreements")
                .Checked(() => !File.Exists(odUser) && !File.Exists(odMachine))
                .Note("Desfazer reinstala o OneDrive pelo winget."));

            L.Add(App("Apps da Microsoft", "app_bing", Level.Essencial, "Notícias, Clima e Bing", "Apps de notícias e clima do Bing que exibem propaganda.", "Microsoft News", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch"));
            L.Add(App("Apps da Microsoft", "app_tips", Level.Essencial, "Dicas, Introdução e Obter Ajuda", "Apps de dicas e suporte que quase ninguém usa.", "Get Help", "Microsoft.Getstarted", "Microsoft.GetHelp", "Microsoft.WindowsFeedbackHub"));
            L.Add(App("Apps da Microsoft", "app_solitaire", Level.Essencial, "Solitaire Collection", "Jogo de cartas pré-instalado com propagandas.", "Microsoft Solitaire Collection", "Microsoft.MicrosoftSolitaireCollection"));
            L.Add(App("Apps da Microsoft", "app_office", Level.Essencial, "Microsoft 365 (Office Hub)", "Atalho/propaganda do Microsoft 365. Não remove o Office se você tiver ele instalado.", "Microsoft 365", "Microsoft.MicrosoftOfficeHub", "Microsoft.OutlookForWindows"));
            L.Add(App("Apps da Microsoft", "app_teams", Level.Recomendado, "Teams (pessoal) e Skype", "Versão gratuita do Teams que abre com o Windows.", "Microsoft Teams", "MSTeams", "MicrosoftTeams", "Microsoft.SkypeApp"));
            L.Add(App("Apps da Microsoft", "app_clipchamp", Level.Recomendado, "Clipchamp", "Editor de vídeo online pré-instalado.", "Clipchamp", "Clipchamp.Clipchamp"));
            L.Add(App("Apps da Microsoft", "app_misc", Level.Recomendado, "Mapas, Pessoas, To Do e Power Automate", "Apps raramente usados num PC gamer.", "Microsoft To Do", "Microsoft.WindowsMaps", "Microsoft.People", "Microsoft.Todos", "Microsoft.PowerAutomateDesktop", "Microsoft.549981C3F5F10", "Microsoft.Windows.DevHome", "Microsoft.MixedReality.Portal", "Microsoft.Microsoft3DViewer"));
            L.Add(App("Apps da Microsoft", "app_family", Level.Recomendado, "Family e Quick Assist", "Controle parental e assistência remota.", "Microsoft Family Safety", "MicrosoftCorporationII.MicrosoftFamily", "MicrosoftCorporationII.QuickAssist"));
            L.Add(App("Apps da Microsoft", "app_phone", Level.Avancado, "Vincular ao Celular", "Remove o Phone Link. Remova só se não usa o celular conectado ao PC.", "Phone Link", "Microsoft.YourPhone"));
            L.Add(App("Apps da Microsoft", "app_media", Level.Avancado, "Filmes e TV / Groove", "Players antigos de mídia da Microsoft.", "Media Player", "Microsoft.ZuneVideo", "Microsoft.ZuneMusic"));
            L.Add(App("Apps da Microsoft", "app_widgets", Level.Avancado, "Pacote de Widgets (Web Experience)", "Remove completamente o pacote que roda os Widgets.", "Windows Web Experience Pack", "MicrosoftWindows.Client.WebExperience"));

            // ===================== DESEMPENHO =====================
            L.Add(T("perf", "Energia e CPU", "ultimate_plan", Level.Essencial, "Plano de energia Desempenho Máximo",
                "Ativa o plano 'Desempenho Máximo' (Ultimate Performance): CPU sem economia de energia, sem estacionamento de núcleos e com resposta imediata.")
                .Checked(PowerPlan.IsUltimateActive)
                .Boost());
            ById2(L, "ultimate_plan").CustomApply = PowerPlan.ApplyUltimate;
            ById2(L, "ultimate_plan").CustomUndo = PowerPlan.Restore;

            L.Add(T("perf", "Energia e CPU", "usb_pcie_power", Level.Recomendado, "Sem economia de energia em USB e PCIe",
                "Desliga a suspensão seletiva de USB e o gerenciamento de energia do PCIe (ASPM) no plano ativo: mouse, teclado, headset e placa de vídeo não 'adormecem' entre um uso e outro, evitando atrasos e desconexões.")
                .Checked(PowerSettings.IsOff)
                .Boost());
            ById2(L, "usb_pcie_power").CustomApply = PowerSettings.Apply;
            ById2(L, "usb_pcie_power").CustomUndo = PowerSettings.Undo;

            L.Add(T("perf", "Segurança x desempenho", "hvci_off", Level.Avancado, "Desligar Integridade de Memória (HVCI) e VBS",
                "Desliga a virtualização de segurança do Windows, que custa de 5 a 10% de FPS em vários jogos. Troca de segurança: o PC fica mais exposto a drivers maliciosos. Alguns anti-cheats e o Windows Hello avançado podem exigir que fique ligado.")
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0)
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0)
                .Reboot());

            L.Add(T("perf", "Energia e CPU", "power_throttling", Level.Recomendado, "Desligar Power Throttling",
                "Impede o Windows de reduzir a CPU de programas em segundo plano (ex.: Discord, OBS) para economizar energia.")
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)
                .Boost());

            L.Add(T("perf", "Energia e CPU", "mmcss", Level.Essencial, "Prioridade de CPU e GPU para jogos",
                "Ajusta o agendador multimídia: mais tempo de CPU para o jogo em primeiro plano, prioridade de GPU 8 e alta prioridade de E/S.")
                .Reg(MMCSS, "SystemResponsiveness", 10)
                .Reg(MMCSS + @"\Tasks\Games", "GPU Priority", 8)
                .Reg(MMCSS + @"\Tasks\Games", "Priority", 6)
                .RegS(MMCSS + @"\Tasks\Games", "Scheduling Category", "High")
                .RegS(MMCSS + @"\Tasks\Games", "SFIO Priority", "High")
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38)
                .Boost());

            L.Add(T("perf", "Energia e CPU", "dynamic_tick", Level.Avancado, "Timer do sistema estável (dynamic tick)",
                "Desliga o 'dynamic tick' para um timer mais consistente (frametime mais estável). Aumenta um pouco o consumo de energia — evite em notebook na bateria.")
                .Ps("bcdedit /set disabledynamictick yes | Out-Null", "bcdedit /deletevalue disabledynamictick | Out-Null")
                .Reboot());

            L.Add(T("perf", "Energia e CPU", "timer_global", Level.Avancado, "Permitir timer de alta resolução global",
                "No Windows 11 o pedido de timer de 0,5 ms só vale para o próprio programa. Isto permite que a opção 'Timer 0,5 ms durante o jogo' (aba Jogos) funcione com o VoltCore na bandeja.")
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)
                .Reboot());

            L.Add(T("perf", "GPU e jogos", "game_mode", Level.Essencial, "Modo de Jogo do Windows",
                "Garante que o Modo de Jogo está ligado: o Windows prioriza o jogo e evita instalar atualizações de driver durante a partida.")
                .Reg(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1)
                .Reg(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1)
                .Boost());

            L.Add(T("perf", "GPU e jogos", "game_dvr", Level.Essencial, "Desligar gravação em segundo plano (Game DVR)",
                "A Xbox Game Bar grava os últimos minutos do jogo o tempo todo, o que custa FPS. Isto desliga essa gravação (a Game Bar continua funcionando).")
                .Reg(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0)
                .Reg(HKLM_POL + @"\GameDVR", "AllowGameDVR", 0)
                .Boost());

            L.Add(T("perf", "GPU e jogos", "hags", Level.Recomendado, "Agendamento de GPU acelerado por hardware",
                "Liga o HAGS: a própria placa de vídeo gerencia sua memória, reduzindo latência. Necessário para o DLSS Frame Generation. Precisa de GPU compatível e reiniciar.")
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2)
                .Reboot()
                .Boost());

            L.Add(T("perf", "GPU e jogos", "windowed_opt", Level.Recomendado, "Otimizações para jogos em janela",
                "Ativa o modelo de apresentação moderno para jogos DX10/11 em janela/sem borda: menos latência e suporte a VRR.")
                .Checked(() => DxGlobal.Get("SwapEffectUpgradeEnable") == "1")
                .Boost());
            ById2(L, "windowed_opt").CustomApply = () => DxGlobal.Set("SwapEffectUpgradeEnable", "1");
            ById2(L, "windowed_opt").CustomUndo = () => DxGlobal.Set("SwapEffectUpgradeEnable", null);

            L.Add(T("perf", "Entrada (mouse e teclado)", "mouse_accel", Level.Essencial, "Desligar aceleração do mouse",
                "Remove o 'Aumentar precisão do ponteiro': o movimento da mira fica 1:1 com a mão, essencial em jogos de tiro.")
                .RegS(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0")
                .RegS(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0")
                .RegS(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0")
                .Boost());

            L.Add(T("perf", "Entrada (mouse e teclado)", "sticky_keys", Level.Recomendado, "Desligar atalho de Teclas de Aderência",
                "Evita que apertar Shift 5 vezes no meio do jogo abra a janela de Teclas de Aderência e minimize o jogo.")
                .RegS(@"HKCU\Control Panel\Accessibility\StickyKeys", "Flags", "506")
                .RegS(@"HKCU\Control Panel\Accessibility\ToggleKeys", "Flags", "58")
                .RegS(@"HKCU\Control Panel\Accessibility\Keyboard Response", "Flags", "122")
                .Boost());

            var nagle = T("perf", "Rede", "nagle", Level.Recomendado, "Menos atraso de rede (algoritmo de Nagle)",
                "Envia pacotes pequenos na hora em vez de agrupá-los. Pode reduzir o ping percebido em jogos online.");
            foreach (var iface in Reg.SubKeys(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces"))
            {
                string p = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + iface;
                object ip = null, dhcp = null;
                try { ip = Reg.Get(p, "IPAddress"); dhcp = Reg.Get(p, "DhcpIPAddress"); } catch { }
                bool hasIp = (ip is string[] && ((string[])ip).Any(s => s.Length > 0 && s != "0.0.0.0")) || (dhcp is string && (string)dhcp != "0.0.0.0");
                if (!hasIp) continue;
                nagle.Reg(p, "TcpAckFrequency", 1).Reg(p, "TCPNoDelay", 1);
            }
            if (nagle.Ops.Count > 0) { nagle.Boost(); L.Add(nagle); }

            L.Add(T("perf", "Rede", "nic_latency", Level.Recomendado, "Placa de rede em modo baixa latência",
                "Desliga Energy Efficient Ethernet, Green Ethernet, modos de economia e a moderação de interrupções da placa de rede, e impede o Windows de desligá-la. Menos variação de ping. A internet cai por alguns segundos enquanto a placa reinicia.")
                .Ps(Nic.ApplyScript(), Nic.UndoScript())
                .Note("Desfazer restaura os valores originais de cada placa.")
                .Boost());

            L.Add(T("perf", "Rede", "net_throttle", Level.Recomendado, "Sem limitação de rede multimídia",
                "Remove o limite que o Windows impõe ao tráfego de rede enquanto toca áudio/vídeo.")
                .Reg(MMCSS, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF))
                .Boost());

            L.Add(T("perf", "Rede", "delivery_opt", Level.Recomendado, "Atualizações sem P2P",
                "Impede o Windows de usar sua internet para enviar atualizações a outros PCs (Otimização de Entrega).")
                .Reg(HKLM_POL + @"\DeliveryOptimization", "DODownloadMode", 0)
                .Boost());

            L.Add(T("perf", "Sistema", "bg_apps", Level.Recomendado, "Apps da Store em segundo plano",
                "Impede que apps da Microsoft Store fiquem rodando em segundo plano gastando CPU e memória.")
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0)
                .Boost());

            L.Add(T("perf", "Sistema", "startup_delay", Level.Essencial, "Iniciar o Windows sem atraso",
                "Remove o atraso artificial que o Windows aplica antes de abrir os programas de inicialização. Também zera o atraso dos menus.")
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0)
                .RegS(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "0")
                .Boost());

            L.Add(T("perf", "Sistema", "notifications", Level.Recomendado, "Menos notificações e sugestões",
                "Desliga dicas do Windows, a tela 'Vamos terminar de configurar' e notificações de sugestão.")
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0)
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.StartupApp", "Enabled", 0));

            L.Add(T("perf", "Sistema", "transparency", Level.Avancado, "Desligar transparência e animações",
                "Tira efeitos de transparência e animações de janelas, liberando um pouco de GPU e deixando o sistema mais responsivo.")
                .Reg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)
                .RegS(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "0")
                .Reg(ADV, "TaskbarAnimations", 0)
                .Reg(@"HKCU\Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0));

            L.Add(T("perf", "Sistema", "sysmain", Level.Avancado, "Desligar SysMain (Superfetch)",
                "Para o pré-carregamento de apps na memória. Em PCs com SSD e pouca RAM costuma liberar memória e reduzir uso de disco.")
                .Reg(SVC + "SysMain", "Start", 4)
                .Ps("Stop-Service SysMain -Force -ErrorAction SilentlyContinue", "Start-Service SysMain -ErrorAction SilentlyContinue"));

            L.Add(T("perf", "Sistema", "search_index", Level.Avancado, "Desligar indexação da Pesquisa",
                "Para o serviço que indexa arquivos o tempo todo. A pesquisa do Windows fica mais lenta para achar arquivos.")
                .Reg(SVC + "WSearch", "Start", 4)
                .Ps("Stop-Service WSearch -Force -ErrorAction SilentlyContinue", "Start-Service WSearch -ErrorAction SilentlyContinue"));

            L.Add(T("perf", "Sistema", "fast_startup", Level.Avancado, "Desligar Inicialização Rápida",
                "Faz o 'Desligar' ser um desligamento completo, evitando acúmulo de problemas de driver e memória entre sessões.")
                .Reg(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0));

            L.Add(T("perf", "Sistema", "hibernation", Level.Avancado, "Desligar hibernação",
                "Apaga o arquivo hiberfil.sys (do tamanho de vários GB) e libera espaço no disco C:. Desliga também a Inicialização Rápida.")
                .Ps("powercfg /hibernate off", "powercfg /hibernate on")
                .Checked(() => !File.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "hiberfil.sys"))));

            return L;
        }

        private static Tweak ById2(List<Tweak> l, string id) { return l.First(t => t.Id == id); }

        /// <summary>Serviços: só inclui os que existem neste PC (nunca cria chaves de serviço novas).</summary>
        private static void AddServices(List<Tweak> l, string id, Level lv, string title, string desc, params string[] services)
        {
            var existing = services.Where(s => Reg.Get(SVC + s, "Start") != null).ToList();
            if (existing.Count == 0) return;
            var t = T("debloat", "Serviços desnecessários", id, lv, title, desc + " (" + string.Join(", ", existing) + ")");
            foreach (var s in existing) t.Reg(SVC + s, "Start", 4);
            string list = string.Join(",", existing.Select(Shell.Q));
            t.Ps("foreach ($s in @(" + list + ")) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }",
                 "foreach ($s in @(" + list + ")) { $st = (Get-ItemProperty \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\$s\" -ErrorAction SilentlyContinue).Start; if ($st -le 2) { Start-Service -Name $s -ErrorAction SilentlyContinue } }");
            l.Add(t);
        }

        private static string TasksPs(bool disable)
        {
            string[] tasks =
            {
                @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
                @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                @"\Microsoft\Windows\Autochk\Proxy",
                @"\Microsoft\Windows\Feedback\Siuf\DmClient",
                @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"
            };
            var parts = tasks.Select(t => "schtasks /Change /TN " + Shell.Q(t) + (disable ? " /Disable" : " /Enable") + " 2>$null | Out-Null");
            string s = string.Join("; ", parts);
            if (disable) s += "; Stop-Service DiagTrack -Force -ErrorAction SilentlyContinue";
            return s;
        }
    }

    /// <summary>Plano de energia Desempenho Máximo.</summary>
    public static class PowerPlan
    {
        private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        public static string ActiveGuid()
        {
            string o;
            Shell.Run("powercfg", "/getactivescheme", out o, 15000);
            var m = Regex.Match(o ?? "", "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        public static string ActiveName()
        {
            string o;
            Shell.Run("powercfg", "/getactivescheme", out o, 15000);
            var m = Regex.Match(o ?? "", @"\((.+)\)");
            return m.Success ? m.Groups[1].Value : "?";
        }

        public static bool IsUltimateActive()
        {
            var a = ActiveGuid();
            if (a == null) return false;
            var mine = Settings.Get("ultimate_guid", "");
            return a == UltimateTemplate || a == mine;
        }

        public static void ApplyUltimate()
        {
            var cur = ActiveGuid();
            if (cur != null && !IsUltimateActive()) Settings.Set("prev_power_plan", cur);
            string guid = Settings.Get("ultimate_guid", "");
            string list;
            Shell.Run("powercfg", "/list", out list, 15000);
            if (guid.Length == 0 || list.IndexOf(guid, StringComparison.OrdinalIgnoreCase) < 0)
            {
                string o;
                Shell.Run("powercfg", "-duplicatescheme " + UltimateTemplate, out o, 15000);
                var m = Regex.Match(o ?? "", "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (!m.Success) throw new Exception("Não foi possível criar o plano Desempenho Máximo: " + o);
                guid = m.Value.ToLowerInvariant();
                Settings.Set("ultimate_guid", guid);
            }
            Shell.Run("powercfg", "/setactive " + guid);
        }

        public static void Restore()
        {
            Shell.Run("powercfg", "/setactive " + Settings.Get("prev_power_plan", Balanced));
        }
    }
}
