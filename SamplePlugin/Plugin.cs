using System;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PartyBlackjack;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Party Blackjack";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    private readonly BlackjackTable table;

    // Config
    private readonly Configuration config;

    // UI
    private readonly WindowSystem windowSystem = new("PartyBlackjackWindows");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        // Load config
        config = (PluginInterface.GetPluginConfig() as Configuration) ?? new Configuration();
        config.Initialize(PluginInterface);

        table = new BlackjackTable(IsLocalPartyLeader);

        // Window with host controls + checkbox
        mainWindow = new MainWindow(
            table,
            canHost: IsLocalPartyLeader,
            start: StartTableFromUi,
            deal: DealFromUi,
            stop: StopFromUi,
            getAutoOpen: () => config.AutoOpenUiWhenTableOpens,
            setAutoOpen: v => { config.AutoOpenUiWhenTableOpens = v; config.Save(); }
        );

        windowSystem.AddWindow(mainWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleUI;

        CommandManager.AddHandler("/bj", new CommandInfo(OnBjCommand)
        {
            HelpMessage =
                "Blackjack host commands:\n" +
                "  /bj start   - open table + deal round\n" +
                "  /bj deal    - deal next round (table persists)\n" +
                "  /bj stop    - close table\n" +
                "  /bj status  - show status (copies to clipboard)\n" +
                "  /bj ui      - toggle window\n" +
                "\nParty commands (type in Party chat):\n" +
                "  !bj join\n" +
                "  !bj bet <amount>\n" +
                "  !bj hit\n" +
                "  !bj stand\n" +
                "  !bj leave\n"
        });

        ChatGui.ChatMessage += OnChatMessage;

        Print("Ready. Party leader can host: /bj start. Toggle UI with /bj ui.");
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;

        CommandManager.RemoveHandler("/bj");

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleUI;

        windowSystem.RemoveAllWindows();
    }

    private void DrawUI() => windowSystem.Draw();
    private void ToggleUI() => mainWindow.IsOpen = !mainWindow.IsOpen;

    // ---- UI button actions ----
    private void StartTableFromUi()
    {
        if (!IsLocalPartyLeader())
        {
            Print("Host-only mode: you must be party leader to host.");
            return;
        }

        table.OpenTableAndDeal();
        Print(table.LastPublicMessage);
        table.CopyLastPublicMessageToClipboard();
        Print("Status copied to clipboard. Paste into /p manually.");

        if (config.AutoOpenUiWhenTableOpens)
            mainWindow.IsOpen = true;
    }

    private void DealFromUi()
    {
        if (!IsLocalPartyLeader())
        {
            Print("Host-only mode: you must be party leader to host.");
            return;
        }

        if (!table.TableOpen)
        {
            Print("No open table. Use Start first.");
            return;
        }

        table.DealNextRound();
        Print(table.LastPublicMessage);
        table.CopyLastPublicMessageToClipboard();
        Print("Status copied to clipboard. Paste into /p manually.");
    }

    private void StopFromUi()
    {
        table.CloseTable();
        Print("Table closed.");
    }

    // ---- /bj command handler ----
    private void OnBjCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim();
        var lower = a.ToLowerInvariant();

        if (lower == "ui")
        {
            ToggleUI();
            return;
        }

        if (lower == "start")
        {
            if (!IsLocalPartyLeader())
            {
                Print("Host-only mode: you must be party leader to host.");
                return;
            }

            table.OpenTableAndDeal();
            Print(table.LastPublicMessage);
            table.CopyLastPublicMessageToClipboard();
            Print("Status copied to clipboard. Paste into /p manually.");

            if (config.AutoOpenUiWhenTableOpens)
                mainWindow.IsOpen = true;

            return;
        }

        if (lower == "deal")
        {
            if (!IsLocalPartyLeader())
            {
                Print("Host-only mode: you must be party leader to host.");
                return;
            }

            if (!table.TableOpen)
            {
                Print("No open table. Use /bj start.");
                return;
            }

            table.DealNextRound();
            Print(table.LastPublicMessage);
            table.CopyLastPublicMessageToClipboard();
            Print("Status copied to clipboard. Paste into /p manually.");
            return;
        }

        if (lower == "stop")
        {
            table.CloseTable();
            Print("Table closed.");
            return;
        }

        if (lower == "status" || lower == "")
        {
            if (!table.TableOpen)
            {
                Print("No open table. Use /bj start.");
                return;
            }

            Print(table.LastPublicMessage);
            table.CopyLastPublicMessageToClipboard();
            Print("Status copied to clipboard. Paste into /p manually.");
            return;
        }

        Print($"Unknown /bj argument: {args}");
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (type != XivChatType.Party)
            return;

        if (!table.TableOpen)
            return;

        // Host-only mode: only process party commands if YOU are leader.
        if (!IsLocalPartyLeader())
            return;

        var senderName = sender.TextValue?.Trim();
        var text = message.TextValue?.Trim();

        if (string.IsNullOrEmpty(senderName) || string.IsNullOrEmpty(text))
            return;

        if (!text.StartsWith("!bj", StringComparison.OrdinalIgnoreCase))
            return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";

        bool changed = sub switch
        {
            "join" => table.Join(senderName),
            "leave" => table.Leave(senderName),
            "bet" => table.Bet(senderName, parts.Length >= 3 ? parts[2] : null),
            "hit" => table.Hit(senderName),
            "stand" => table.Stand(senderName),
            _ => false
        };

        if (!changed)
            return;

        Print(table.LastPublicMessage);
        table.CopyLastPublicMessageToClipboard();
        Print("Updated status copied to clipboard. Paste into /p manually.");
    }

    private bool IsLocalPartyLeader()
    {
        var me = ClientState.LocalPlayer?.Name.TextValue;
        if (string.IsNullOrWhiteSpace(me))
            return false;

        // Solo testing
        if (PartyList == null || PartyList.Length == 0)
            return true;

        // Simple heuristic: party index 0 is leader
        var leaderName = PartyList[0]?.Name?.TextValue;
        return !string.IsNullOrWhiteSpace(leaderName) &&
               string.Equals(leaderName, me, StringComparison.OrdinalIgnoreCase);
    }

    private static void Print(string msg) => ChatGui.Print(msg, "BJ");
}
