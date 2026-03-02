using System;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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
            IsLocalPartyLeader,
            StartTableFromUi,
            DealFromUi,
            StopFromUi,
            () => config.AutoOpenUiWhenTableOpens,
            v => { config.AutoOpenUiWhenTableOpens = v; config.Save(); }
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
                "  !bj double  (on initial hand if affordable)\n" +
                "  !bj leave"
        });

        ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler("/bj");

        windowSystem.RemoveAllWindows();

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleUI;
    }

    private void OnBjCommand(string command, string args)
    {
        var argTokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (argTokens.Length == 0)
        {
            ToggleUI();
            return;
        }

        var sub = argTokens[0].ToLowerInvariant();

        if (sub == "ui")
        {
            ToggleUI();
            return;
        }

        if (sub == "start")
        {
            StartTableFromUi();
            return;
        }

        if (sub == "deal")
        {
            DealFromUi();
            return;
        }

        if (sub == "stop")
        {
            StopFromUi();
            return;
        }

        if (sub == "status")
        {
            table.CopyPublicMessageAsPartyCommandToClipboard();
            ChatGui.Print("Status copied to clipboard as /p command.");
            return;
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (type != XivChatType.Party)
            return;

        if (!table.TableOpen)
            return;

        if (!IsLocalPartyLeader())
            return;

        var senderName = sender.GetPayloadText<PayloadType.Player>()?.Text ?? ""; // Simplified, assume player payload

        if (string.IsNullOrEmpty(senderName))
            return;

        var msgText = message.TextValue.Trim().ToLowerInvariant();

        if (!msgText.StartsWith("!bj "))
            return;

        var cmd = msgText.Substring(4).Trim();
        var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
            return;

        var subCmd = tokens[0];

        bool handled = subCmd switch
        {
            "join" => table.Join(senderName),
            "leave" => table.Leave(senderName),
            "bet" => table.Bet(senderName, tokens.Length > 1 ? tokens[1] : null),
            "hit" => table.Hit(senderName),
            "stand" => table.Stand(senderName),
            "double" => table.Double(senderName),
            _ => false
        };

        if (handled)
        {
            isHandled = true; // Optional: suppress the message if desired
            table.CopyPublicMessageAsPartyCommandToClipboard(); // Auto-copy update
        }
    }

    private bool IsLocalPartyLeader()
    {
        if (ClientState.LocalPlayer == null)
            return false;

        if (PartyList.Length == 0)
            return true; // Solo is leader

        var leader = PartyList[0]; // First is leader
        return leader.Name == ClientState.LocalPlayer.Name;
    }

    private void StartTableFromUi()
    {
        if (!IsLocalPartyLeader())
            return;

        table.OpenTableAndDeal();

        if (config.AutoOpenUiWhenTableOpens)
            mainWindow.IsOpen = true;
    }

    private void DealFromUi()
    {
        if (!IsLocalPartyLeader())
            return;

        table.DealNextRound();
    }

    private void StopFromUi()
    {
        if (!IsLocalPartyLeader())
            return;

        table.CloseTable();
    }

    private void DrawUI() => windowSystem.Draw();

    private void ToggleUI() => mainWindow.Toggle();
}
